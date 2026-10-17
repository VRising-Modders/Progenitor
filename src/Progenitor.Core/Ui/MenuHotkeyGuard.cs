using System;

namespace Progenitor.Ui;

/// <summary>
/// Which <c>RotateCameraBlocker</c> instance <c>GameInputBlocker.Mask</c> was entered for
/// (job KEY3). The detour is process-wide and fires for every instance in the process — the
/// game's own as well as Progenitor's two — so the instance pointer is the whole of the
/// authorization, and now it also selects <i>which</i> recipe is applied. See
/// <see cref="MenuHotkeyGuard.TargetFor"/>.
/// </summary>
internal enum HotkeyMaskTarget
{
    /// <summary>A context that is not ours. The mask applies nothing at all.</summary>
    None,

    /// <summary>
    /// The order-599 context (<see cref="UiInputCapture.Order"/>): the full FUI20/FUI27 recipe —
    /// <c>BlockAllKeys</c>, Move/Aim, zoom, the close actions, and KEY2's hotkey block, which is
    /// still what covers <c>MenuInputSystem</c> at order 600.
    /// </summary>
    Ours,

    /// <summary>
    /// The order-498 context (<see cref="UiInputCapture.HotkeyGuardOrder"/>): the hotkey set and
    /// <b>nothing else</b>, and only while <see cref="MenuHotkeyGuard.HotkeysBlocked"/>. It exists
    /// solely to be walked before <c>FullscreenMenuMapper</c> at 502; giving it any part of the 599
    /// recipe would silently move Progenitor's whole input mask ahead of every HUD menu.
    /// </summary>
    Guard,

    /// <summary>
    /// CHAT1: the order-97 context (<see cref="UiInputCapture.ChatClickGuardOrder"/>): the mouse-bound
    /// gameplay actions in <c>Progenitor.Chat.ChatClickGuard.BlockedActions</c> while <c>ChatClickGuard.Active</c>
    /// (a press that starts over the visible chat box), the camera zoom axis while <c>ChatClickGuard.WheelBlocked</c>
    /// (the pointer is over the box), and <b>nothing else</b> (<c>Progenitor.Chat.ChatMaskPlan</c>).
    /// </summary>
    Chat,
}

/// <summary>
/// Job KEY2. While a Progenitor text field has owned focus, the fullscreen menu's letter
/// hotkeys must not act: typing <c>p</c> into the Mods search put a <c>p</c> in the field
/// <b>and</b> switched the menu to Social, because <c>P</c> is the vanilla shortcut for it.
///
/// <para>
/// <b>The path the key takes.</b> The outer-tab letters are not a "switch tab" action and not a
/// uGUI concern: each menu has its own <c>ProjectM.ButtonInputAction</c> member in the 18-26 block
/// (client metadata), and <c>ProjectM.MenuInputSystem</c>
/// turns them into an open. Its only real entry point is
/// <c>HandleInput(InputState)</c> (parameter by
/// value), which nothing but <c>InputActionSystem</c> calls — after it has subtracted every
/// registered context's <c>BlockInputState</c>. Its own <c>OnUpdate</c> is the shared empty stub,
/// and so is its <c>GetConsumedInputs</c>, so it consumes nothing and
/// blocks nobody. That is the same shape as Escape in job FUI20: the key can be taken cleanly, and
/// the context list is the only place it can be taken.
/// </para>
///
/// <para>
/// <b>Why the existing mask did not already take it.</b> <c>GameInputBlocker.Mask</c> calls
/// <c>BlockAllKeys(false)</c> in <see cref="UiMaskScope.Full"/>, which looks like it should cover
/// action 23. Two things defeat it, and the fix does not need to know which one was operating.
/// First, <c>BlockInputState</c> carries <i>two</i> lists — <c>BlockedButtonInputActions</c> and
/// <c>BlockedNewButtonInputActions</c> — with
/// <c>BlockKey</c> and <c>BlockKeyDown</c> writing one each. A menu shortcut is a press, so it is the
/// <i>down</i> list that governs it, and whether <c>BlockAllKeys</c> fills both
/// cannot be read offline: the metadata has the signature, not the body. Second, the default
/// <c>includeUINavigation = false</c> exempts
/// <c>ButtonInputActionCollection.UINavigationActions</c>, whose contents
/// are filled by a <c>.cctor</c> the metadata does not contain. So this guard names its actions
/// explicitly and blocks each one in <b>both</b> lists — the same "never depend on the unreadable
/// <c>.cctor</c>" rule <see cref="InputMaskPlan.BlockedCloseActions"/> was written under.
/// </para>
///
/// <para>
/// <b>Why a flag and not a second context.</b> A second <c>IInputContext</c> would have to be
/// added and removed on the game's ordered list every time focus moved between two fields or a
/// click landed outside one — inside the window where <c>InputActionSystem</c> sets
/// <c>_DenyModificationsOfInputContexts</c> and refuses the mutation (see
/// <see cref="IUiInputBlocker"/>), so every focus change would inherit the deferral machinery
/// <see cref="UiInputCapture"/> exists to run. The mask our already-registered context applies is
/// re-read every frame, exactly as <see cref="UiMaskScope"/> is since job FUI20, so focus changes
/// a flag instead. It also means there is no teardown that can leak: nothing is registered, so
/// nothing can be left registered.
/// </para>
///
/// <para>
/// <b>Job KEY3: the flag was right and the place it was applied was not.</b> KEY2 shipped the
/// paragraph above and it is still the rule for the <i>flag</i> — focus changes a flag, never a
/// registration. What it got wrong is which context reads that flag. KEY2 put the block on the
/// one context Progenitor already had, at <see cref="UiInputCapture.Order"/> (599), and live on
/// 2026-09-12 a scan-code <c>P</c> with the Install search owned-focused
/// (<c>status.focus {field: install-search, hotkeysBlocked: true}</c>) still switched the
/// FullscreenMenu to Social and closed the workspace.
/// </para>
///
/// <para>
/// <b>Why 599 could never have worked, from the native side.</b> The in-menu path is not
/// <c>MenuInputSystem</c> at all: <c>ProjectM.UI.FullscreenMenuMapper</c>
/// (client) is itself a <c>SystemBase, IInputContext</c>, and its native
/// <c>OnStartRunning</c> registers it with
/// <c>InputActionSystem.AddInputContext(this, world, 0x1f6)</c> —
/// 502 = <c>InputContextOrder.HUDMenu</c>. The native <c>InputActionSystem.OnUpdate</c> walks
/// <c>_InputContexts</c> <b>ascending</b> from
/// index 0 and each context subtracts inputs from the ones after it. A context at 599 is walked <i>after</i> 502, so nothing it blocks can
/// be removed from what the mapper already saw. KEY2's mask is not wrong, it is late.
/// </para>
///
/// <para>
/// <b>So KEY3 adds a second context, at <see cref="UiInputCapture.HotkeyGuardOrder"/> (498),
/// strictly ahead of the mapper.</b> It is not a second <i>flag</i> and it is not added or removed
/// on focus: it is registered and removed at exactly the same points as the 599 context, and what
/// is focus-scoped is only the mask it applies (this flag, read per frame, exactly as before). So
/// the paragraph above survives intact — no focus change touches the game's ordered list — while
/// the block finally lands somewhere the mapper can be subtracted from. 498 is chosen because
/// 496-498 are the only free integers below the mapper with no named order to tie against
/// (<c>InputContextOrder</c>: HUDElement 495, GenericPopup 499,
/// DebugMenu 500, HUDSubMenu 501, HUDMenu 502, MenuInput 600), and <c>AddInputContext</c> inserts
/// at the first index whose order is ≥ the new one, so a tie's processing position would depend on
/// registration timing — the same reason job FUI20 moved off 600. The 599 block stays as well: it
/// is what covers <c>MenuInputSystem</c> at order 600, the out-of-menu path where <c>P</c> opens
/// Social with no menu up.
/// </para>
///
/// <para>
/// <b>The one thing that could not be settled offline.</b> The mapper's native <c>HandleInput</c>
/// reads the outer-tab letters
/// from <b><c>InputsUp</c></b> — key release — while <c>BlockInputState</c>
/// offers <c>BlockedButtonInputActions</c> (<c>BlockKey</c>) and
/// <c>BlockedNewButtonInputActions</c> (<c>BlockKeyDown</c>) and <b>no <c>BlockKeyUp</c></b>.
/// Whether <c>BlockKey</c> removes an action from a later context's <c>InputsUp</c> is a property
/// of a native body the metadata does not contain, so it cannot be read here. Both lists are therefore
/// written (as KEY2 already did), the application is <i>counted</i> so the live check can tell
/// "the guard ran and the tab still moved" apart from "the guard never ran"
/// (<see cref="NoteMaskApplied"/>, reported as <c>status.focus.guard</c>), and the fallback for a
/// negative answer is pre-built and switched off: see <see cref="UseSetStatePrefix"/>.
/// </para>
///
/// <para>
/// <b>What it is deliberately not.</b> It is not a global key swallow — only the eight menu-open
/// actions, Interact, and the four in-menu tab-cycling actions below are taken, and only while a field is
/// focused. Movement, abilities and the Escape close key are untouched, and with no
/// field focused this contributes nothing at all. It also does not change
/// <c>UnityKeyInputSource.TextFieldFocused</c>, which gates Progenitor's own keybinds and answers
/// a different question. <c>Keybinds</c> reads <see cref="HotkeysBlocked"/> for one narrower rule
/// (TEXTUNDO1): while a field has owned focus, a bind whose chord the field itself takes
/// (<see cref="UiTypedKeys.FieldTakes"/>: a printable key, Ctrl plus a letter, an editing key) does
/// not fire, and every other bind (F10, the mouse's Back and Forward) still does.
/// </para>
/// </summary>
internal static class MenuHotkeyGuard
{
    /// <summary>No Progenitor field has owned focus. The value <see cref="FocusedField"/> reports.</summary>
    internal const string NoField = "none";

    /// <summary>The Mods sidebar search row (<c>MenuRowTone.Search</c>).</summary>
    internal const string ModsSearch = "mods-search";

    /// <summary>The Install toolbar input cell (<c>CellKind.Input</c>, LIST3).</summary>
    internal const string InstallSearch = "install-search";

    /// <summary>BUILDSEARCH1: the search field Core adds to the game's build menu.</summary>
    internal const string BuildSearch = "build-search";

    /// <summary>
    /// The fullscreen-menu open shortcuts (client metadata). There is no
    /// <c>OpenSocial</c> or <c>SwitchMenuTab</c> on this build: one action per menu, and
    /// <c>ClanMenu = 23</c> is the reported one (<c>P</c> → Social). The whole 18-26 block
    /// is taken rather than only 23, because every other letter in it is the same defect waiting
    /// for a different keystroke.
    ///
    /// <para>
    /// Plain <c>int</c>s, not the interop enum, for the reason <see cref="ButtonInputActions"/>
    /// gives — this file is compiled into the test assembly, which has no game references — and
    /// because job FUI27 made the mask uniformly metadata-read constants so a renumbering cannot leave
    /// half the values cross-checked and half not.
    /// </para>
    ///
    /// <para>
    /// <c>ButtonInputActionCollection.GameplayMenuActions</c> is very
    /// likely this same set, but its contents come from a <c>.cctor</c> whose body the metadata does
    /// not contain, so it is not read here: the block would then be a guess about native static
    /// state instead of a list.
    /// </para>
    ///
    /// <para>
    /// <b>Job KEY3 re-derived this list from the in-menu consumer and it did not move.</b> KEY2 read
    /// it from <c>MenuInputSystem</c> (order 600, the out-of-menu path). The context added at
    /// <see cref="UiInputCapture.HotkeyGuardOrder"/> exists for a different consumer —
    /// <c>FullscreenMenuMapper.HandleInput</c> — so
    /// every id was checked against what that native body actually polls:
    /// <c>0x12</c> VBloodMenu, <c>0x16</c> MapMenu (also on the down list),
    /// <c>0x17</c> ClanMenu, <c>0x18</c> SpellBookMenu, <c>0x19</c> EmotesMenu,
    /// <c>0x1a</c> VampirePowersMenu, then <c>0x3ee-0x3f1</c> Menu_TabLeft/Right/Minor.
    /// Every one of those is already here.
    /// </para>
    ///
    /// <para>
    /// Three ids the brief raised and this list deliberately still does <b>not</b> take.
    /// <c>0x11 = 17</c> is <c>Back</c>, which the mapper does read and
    /// which <see cref="InputMaskPlan.BlockedCloseActions"/> already takes in every scope — adding it
    /// here would make two owners of the panel's close gesture, which is the one thing
    /// <c>The_blocked_set_takes_neither_the_close_key_nor_gameplay_actions</c> asserts against.
    /// <c>0x3ec</c> is <b>not</b> a tab action: it is <c>Menu_Close = 1004</c>,
    /// also read by the mapper, and also already owned by
    /// <c>BlockedCloseActions</c>. And <c>0x13 = 19</c> is <c>SelectProgression</c>,
    /// which is why the 18-26 "block" is 18, 20-26 and has a hole in it.
    /// <c>20 CharacterMenu</c> and <c>21 BuildMenu</c> are kept although the mapper does not poll
    /// them, because they are real menu-open actions on the 600 path KEY2 was aimed at.
    /// </para>
    /// </summary>
    private static readonly int[] MenuOpenActions =
    {
        // ContainerMenuMapper.HandleInput closes on keyboard/mouse Interact in InputsUp.
        // Block both action lists through the existing early focus guard, before HUD menus read it.
        1,    // Interact (F by default; action-based so remapped keys are covered)
        18,   // VBloodMenu
        20,   // CharacterMenu
        21,   // BuildMenu
        22,   // MapMenu
        23,   // ClanMenu (Social) — the reported key, P
        24,   // SpellBookMenu
        25,   // EmotesMenu
        26,   // VampirePowersMenu

        // The in-menu tab cycling (client metadata). Not letter hotkeys, but the same
        // defect on a gamepad or the shoulder-button bindings: the menu must not change tab under
        // a field that is taking text.
        1006, // Menu_TabLeft
        1007, // Menu_TabRight
        1008, // Menu_TabLeftMinor
        1009, // Menu_TabRightMinor
    };

    /// <summary>
    /// The id of the field that currently has owned focus, or <see cref="NoField"/>. Written by
    /// the shown-input pump every frame and by the focus writers on the spot; read with no lock
    /// from a native callback, so it is a single reference store of an interned constant.
    /// </summary>
    private static volatile string _focused = NoField;

    /// <summary>
    /// Which Progenitor field has owned focus: <see cref="ModsSearch"/>,
    /// <see cref="InstallSearch"/> or <see cref="NoField"/>. Reported by
    /// <c>mods_ui status.focus.field</c>.
    /// </summary>
    internal static string FocusedField => _focused;

    /// <summary>
    /// Whether the menu hotkeys are being taken this frame. Derived from
    /// <see cref="FocusedField"/> and never stored separately: a second copy of this answer is how
    /// the pointer gate and the mask scope drifted apart before job FUI23, and the whole release
    /// story here is "the flag follows focus", which only holds if there is one flag.
    /// </summary>
    internal static bool HotkeysBlocked =>
        !string.Equals(_focused, NoField, System.StringComparison.Ordinal);

    /// <summary>
    /// The actions the mask takes while <see cref="HotkeysBlocked"/>. The live array, not a copy:
    /// the caller is a native callback on the game's per-frame input path and must not allocate
    /// there — the same contract, and the same prohibition on mutating it, as
    /// <see cref="InputMaskPlan.BlockedCloseActions"/>.
    /// </summary>
    internal static int[] BlockedActions() => MenuOpenActions;

    /// <summary>
    /// The single writer. <paramref name="field"/> is one of the ids above; anything else is
    /// treated as <see cref="NoField"/>, so a caller that grows a new field id without teaching
    /// this method about it fails <i>open</i> — the menu keeps working and the field takes text it
    /// also passes on, which is today's defect rather than a dead menu the player cannot escape.
    ///
    /// <para>
    /// Called from the shown-input pump every frame with the pump's own
    /// <c>FocusedArmedField()</c> answer, which is what makes every unfocus path — Escape, a click
    /// elsewhere, the field being destroyed by a rebuild, the workspace closing, the pump
    /// unsubscribing, Core unload — releasing without any of them having to know this type exists.
    /// The explicit calls on the focus writers only make the release immediate instead of
    /// one frame late.
    /// </para>
    /// </summary>
    internal static void SetFocusedField(string? field)
    {
        _focused = field switch
        {
            ModsSearch => ModsSearch,
            InstallSearch => InstallSearch,
            BuildSearch => BuildSearch,
            _ => NoField,
        };
    }

    /// <summary>Focus released, from whatever path noticed. Same writer, named for the caller.</summary>
    internal static void Release()
    {
        SetFocusedField(null);
        _closeHeld = false;
    }

    // ---- KEYS3 polish: the game's menu must not close on an Escape a capture or a dialog took ----

    /// <summary>
    /// Whether the 498 guard also takes the close actions (<see cref="InputMaskPlan.BlockedCloseActions"/>:
    /// <c>Back</c> 17, <c>Menu_Close</c> 1004) this frame.
    ///
    /// <para>
    /// <b>Why (live 2026-09-30, P2).</b> In a world the workspace is hosted in the game's own
    /// FullscreenMenu. An Escape during a key capture was correctly left to the capture by
    /// <c>Ui.OnBootstrapKey</c>, but <c>FullscreenMenuMapper.HandleInput</c> (order 502) also read
    /// <c>Back</c> (<c>…$$HandleInput.c</c> :127) and <c>Menu_Close</c> (:135) and closed the menu,
    /// which closed the workspace (<c>reason=native-detach:menu-closed</c>). The 599 context's close
    /// block is walked after 502, so it is late for the mapper — the same finding as job KEY3 for the
    /// letter hotkeys. The fix reuses KEY3's context at 498, ahead of the mapper, and only while
    /// this flag is true, so an ordinary Escape still closes the menu as it always did.
    /// </para>
    ///
    /// <para>
    /// A flag written by the shown-input pump each frame (<see cref="NextCloseHold"/>), read with no
    /// lock from the native callback; no context is added or removed for it.
    /// </para>
    /// </summary>
    internal static bool CloseHeld => _closeHeld;

    private static volatile bool _closeHeld;

    private static volatile bool _closeHoldLogged;

    /// <summary>True the first time the close hold was applied in a hold (for one log line per hold).</summary>
    internal static bool NoteCloseHoldApplied()
    {
        if (_closeHoldLogged) return false;
        _closeHoldLogged = true;
        return true;
    }

    /// <summary>The pump's single writer of <see cref="CloseHeld"/>.</summary>
    internal static void SetCloseHeld(bool held)
    {
        if (!held) _closeHoldLogged = false;
        _closeHeld = held;
    }

    /// <summary>
    /// Hold the close actions while a key capture owns Escape (waiting, or just ended on it) or a
    /// framework dialog is open, and keep holding while the Escape that started it is still down, so
    /// neither its press nor its release reaches the game's menu. Released on the first frame none of
    /// those is true.
    /// </summary>
    internal static bool NextCloseHold(bool captureOwnsEscape, bool dialogOpen, bool escapeHeld, bool wasHeld) =>
        WorkspaceInputPolicy.HoldsGameClose(captureOwnsEscape, dialogOpen, escapeHeld, wasHeld);

    // ---- job KEY3: which instance, and did it ever run ----------------------------------------

    /// <summary>
    /// Which recipe <c>GameInputBlocker.Mask</c> should apply for the instance it was entered for.
    /// A pure function over three pointers so the branch that keeps Progenitor's full input mask off
    /// the 498 context — and both recipes off the game's own contexts — is testable without a client.
    ///
    /// <para>
    /// <b>Zero is never a match, on any of the three.</b> <c>_ours</c> and <c>_guard</c> are zeroed
    /// on every drop and teardown path precisely so that a frame already inside the detour touches
    /// nothing, and a native callback can be handed a null <c>instance</c>. Comparing a zeroed
    /// pointer against a zeroed field would turn each of those release paths into an
    /// "everything matches" path, which is the opposite of fail-closed.
    /// </para>
    ///
    /// <para>
    /// <b>And <c>ours</c> wins a collision.</b> The two pointers cannot be equal in practice — they
    /// are two separately constructed IL2CPP objects — but if a bug ever made them so, applying the
    /// 599 recipe is the conservative answer: it is a superset of the guard's, so the 498 context
    /// would over-block rather than the 599 context under-block and let Escape through.
    /// </para>
    /// </summary>
    internal static HotkeyMaskTarget TargetFor(IntPtr instance, IntPtr ours, IntPtr guard)
        => TargetFor(instance, ours, guard, IntPtr.Zero);

    /// <summary>
    /// CHAT1: the same answer with the chat click guard's pointer as a fourth candidate. It is checked
    /// last, so <c>ours</c> and <c>guard</c> keep winning a collision for the reasons above; zero never
    /// matches here either.
    /// </summary>
    internal static HotkeyMaskTarget TargetFor(IntPtr instance, IntPtr ours, IntPtr guard, IntPtr chat)
    {
        if (instance == IntPtr.Zero) return HotkeyMaskTarget.None;
        if (ours != IntPtr.Zero && instance == ours) return HotkeyMaskTarget.Ours;
        if (guard != IntPtr.Zero && instance == guard) return HotkeyMaskTarget.Guard;
        if (chat != IntPtr.Zero && instance == chat) return HotkeyMaskTarget.Chat;
        return HotkeyMaskTarget.None;
    }

    /// <summary>
    /// How long one <c>masked</c> window is. One second, because the readback's question is "is the
    /// guard running right now" and the answer has to be readable from a single
    /// <c>mods_ui status</c> call taken while a key is held.
    /// </summary>
    internal const int MaskWindowMs = 1000;

    /// <summary>
    /// Guards the three counter fields below. A <c>Monitor</c> on the game's per-frame input path is
    /// deliberate and is the smaller of two evils: it is entered once per frame per registered
    /// context, always uncontended (one writer, and the readers are Dev ops), and the alternative —
    /// a lock-free window over three unsynchronized fields — is a counter that can report a torn
    /// window, which for the one instrument whose whole job is to settle the unreadable
    /// <c>InputsUp</c> question would be worse than useless.
    /// </summary>
    private static readonly object CounterGate = new();

    private static long _windowStartMs;
    private static int _windowCount;
    private static bool _everApplied;

    /// <summary>
    /// Whether the 498 context is currently in the game's ordered list, as
    /// <c>GameInputBlocker</c> last observed through <c>IsContextRegistered</c>. Mirrored here
    /// rather than read live because <c>status.focus.guard</c> is a Dev readback that must not reach
    /// into <c>InputActionSystem</c> from whatever thread it is on — the same rule
    /// <c>UiInputCapture.IsRegistered</c> is under.
    /// </summary>
    private static volatile bool _guardRegistered;

    /// <summary>
    /// See <see cref="_guardRegistered"/>. Written only by <c>GameInputBlocker</c>'s register and
    /// remove paths, from their verified answer and never from "nothing threw".
    /// </summary>
    internal static bool GuardRegistered
    {
        get => _guardRegistered;
        set => _guardRegistered = value;
    }

    /// <summary>
    /// Count one application of the guard's mask. <b>Returns true exactly once per process</b>, on
    /// the first application, so the caller can write the one log line deliverable 3 asks for
    /// without keeping a second flag beside this one.
    ///
    /// <para>
    /// <paramref name="nowMs"/> is a monotonic millisecond stamp (<c>Environment.TickCount64</c> in
    /// production, an explicit value in tests). A window older than <see cref="MaskWindowMs"/>
    /// restarts rather than decaying: a rolling average would hide the state this exists to catch,
    /// which is a guard that ran on exactly the frames the tab moved.
    /// </para>
    /// </summary>
    internal static bool NoteMaskApplied(long nowMs)
    {
        lock (CounterGate)
        {
            if (_windowCount == 0 || nowMs - _windowStartMs >= MaskWindowMs || nowMs < _windowStartMs)
            {
                _windowStartMs = nowMs;
                _windowCount = 0;
            }

            _windowCount++;
            if (_everApplied) return false;
            _everApplied = true;
            return true;
        }
    }

    /// <summary>
    /// Applications of the guard's mask in the current <see cref="MaskWindowMs"/> window, or zero
    /// once that window has gone stale. Stale-reads-as-zero is the point: a reader taking
    /// <c>status.focus.guard</c> a minute after the last press must see <c>masked: 0</c>, not the
    /// count from whenever the guard last ran.
    /// </summary>
    internal static int MaskedThisSecond(long nowMs)
    {
        lock (CounterGate)
        {
            if (_windowCount == 0) return 0;
            if (nowMs - _windowStartMs >= MaskWindowMs || nowMs < _windowStartMs) return 0;
            return _windowCount;
        }
    }

    /// <summary>Whether the guard's mask has been applied at all in this process.</summary>
    internal static bool EverApplied
    {
        get { lock (CounterGate) return _everApplied; }
    }

    // ---- job KEY3 deliverable 3: the fallback, built and switched off --------------------------

    /// <summary>
    /// Whether to also prefix <c>FullscreenMenuMapper.SetState</c>
    /// (<c>private void SetState(FullscreenMenuState state)</c> — <b>not</b> the method the brief
    /// named, which is <c>GamepadTabInnerNavigation(int)</c>).
    ///
    /// <para>
    /// <b>Why this exists switched off.</b> The 498 context writes <c>BlockKey</c> and
    /// <c>BlockKeyDown</c>, and the mapper reads the outer-tab letters from <c>InputsUp</c>
    /// (its native <c>HandleInput</c>) — a third list with no
    /// <c>BlockKeyUp</c> to write it. Whether <c>BlockKey</c> subtracts from
    /// a later context's <c>InputsUp</c> as well as its <c>InputsDown</c> is a fact about a native
    /// body the metadata does not contain, and no amount of offline reading settles it. So the ordering
    /// fix ships alone, <see cref="NoteMaskApplied"/> makes the live answer unambiguous, and this
    /// flag is the second half's <i>decision</i>, already written and tested:
    /// <see cref="ShouldSuppressSetState"/> is the whole condition, so the live check's answer costs
    /// no design work.
    /// </para>
    ///
    /// <para>
    /// <b>What flipping this constant does NOT do, stated plainly.</b> There is no patch installed on
    /// <c>SetState</c> and this job did not build one, so setting this <c>true</c> today changes
    /// nothing at runtime — it only makes <see cref="ShouldSuppressSetState"/> start answering
    /// <c>true</c> to a caller that does not yet exist. The missing half is the seam: a prefix on a
    /// <c>private</c> instance method of an IL2CPP system needs a patch owner
    /// (<c>ctx.Patches</c> is a plugin-context facade and this is Core-internal UI code), and the
    /// sites that would have to be reached from it — <c>NativeOuterTabRegistration</c>'s typed
    /// <c>SetState</c> calls, which need the <paramref name="progenitorCall"/> bypass threaded through
    /// them — are outside the ordering fix. The parameter list is single-<c>int</c>-by-value
    /// (<c>FullscreenMenuState</c>), so no by-ref struct hazard stands in the way; what is owed is a
    /// Bootstrap-owned patch slot for it. Read
    /// this paragraph before promising a coordinator that one constant is the fix.
    /// </para>
    ///
    /// <para>
    /// It is deliberately a <c>const</c> and not config. A prefix on the mapper's own state machine
    /// is a bigger commitment than a mask — it can strand the fullscreen menu on a tab if its
    /// conditions are ever wrong — so flipping it is a code change that goes through a build and a
    /// review, not a setting a player can find.
    /// </para>
    /// </summary>
    internal const bool UseSetStatePrefix = false;

    /// <summary>
    /// Whether a <c>SetState</c> call should be suppressed by the fallback prefix. Pure, and every
    /// term is required — this is the whole of the fallback's safety argument, so it is one function
    /// with one test per term rather than a condition spread through a patch body.
    ///
    /// <para>
    /// <paramref name="usePrefix"/> is <see cref="UseSetStatePrefix"/>: off, nothing is ever
    /// suppressed, which is what makes shipping the flag switched off a no-op rather than a promise.
    /// <paramref name="hotkeysBlocked"/> is <see cref="HotkeysBlocked"/>: with no Progenitor field
    /// focused the menu must behave exactly as vanilla. <paramref name="progenitorCall"/> is the
    /// bypass — <c>NativeOuterTabRegistration</c> drives the owned page with a typed
    /// <c>SetState(7)</c> (<c>NativeOuterTabRegistration.cs:1216</c>) and its return transition
    /// (<c>:1246</c>), and a prefix that swallowed those would leave the workspace unable to open
    /// itself from the very field whose focus armed the guard.
    /// </para>
    ///
    /// <para>
    /// And <c>requested == current</c> is let through rather than suppressed: the mapper's own
    /// <c>SetState</c> is not idempotent (it destroys and rebuilds the view instance — see
    /// <c>NativeTabSelection</c>), so a same-state call is a refresh some other subsystem is asking
    /// for and not a tab change a typing player could have caused. Suppressing it would make this
    /// prefix a general-purpose brake on the menu's state machine, which is exactly what it must
    /// not become.
    /// </para>
    /// </summary>
    internal static bool ShouldSuppressSetState(
        bool usePrefix, bool hotkeysBlocked, int requested, int current, bool progenitorCall)
    {
        if (!usePrefix) return false;
        if (!hotkeysBlocked) return false;
        if (progenitorCall) return false;
        return requested != current;
    }

#if PROGENITOR_TESTS
    /// <summary>
    /// Back to a fresh-process state. The counters are process-static and
    /// <see cref="NoteMaskApplied"/>'s "true exactly once" contract is over the process, so a test
    /// asserting the first-application log line needs the window <i>and</i> the latch cleared.
    /// </summary>
    internal static void ResetCountersForTests()
    {
        lock (CounterGate)
        {
            _windowStartMs = 0;
            _windowCount = 0;
            _everApplied = false;
        }

        _guardRegistered = false;
    }
#endif
}
