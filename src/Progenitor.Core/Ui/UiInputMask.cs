namespace Progenitor.Ui;

/// <summary>
/// The <c>ProjectM.ButtonInputAction</c> values this module names, as the client metadata
/// declares them. They are plain <c>int</c>s here rather than the interop enum because this
/// file is compiled into the test assembly, which has no game references — the same reason
/// <see cref="UnityUiRuntimeMetrics"/> exists.
///
/// <para>
/// <c>ButtonInputAction</c> is <b>not</b> <c>[Flags]</c> (in the client metadata its
/// backing field is a plain <c>int</c>, unlike <c>AnalogInputAction</c>,
/// which is). Actions are therefore blocked one at a time into
/// <c>BlockInputState.BlockedButtonInputActions</c>, a <c>NativeList&lt;ButtonInputAction&gt;</c>,
/// and never OR-ed together.
/// </para>
/// </summary>
internal static class ButtonInputActions
{
    /// <summary>
    /// Vanilla close / Escape. The action
    /// <c>ProjectM.MenuInputSystem</c> turns into the system menu, and the one Bootstrap's
    /// <c>UiInputContext</c> forwards to <see cref="Ui"/> as the panel-close gesture.
    /// </summary>
    internal const int Back = 17;

    /// <summary>
    /// The UI-navigation close — a <i>different</i> action from
    /// <see cref="Back"/>, in the 1000-block with the rest of the menu navigation. The Escape key
    /// carries the Unity usages <c>"Back"</c> <b>and</b> <c>"Cancel"</c> in the client
    /// metadata, so one physical press can arrive as either. Blocking only
    /// <see cref="Back"/> is the shape of a fix that works on the developer's machine and half
    /// works on a player's.
    /// </summary>
    internal const int MenuClose = 1004;

    /// <summary>Camera rotation. Allowed back on keyboard and mouse, exactly as chat does.</summary>
    internal const int RotateCamera = 500;
}

/// <summary>
/// <c>ProjectM.AnalogInputAction</c> values this module names. Unlike
/// <see cref="ButtonInputActions"/> these <b>are</b> flags and
/// are OR-ed into <c>BlockInputState.BlockedAnalogInputActions</c>.
/// </summary>
internal static class AnalogInputActions
{
    /// <summary>
    /// Camera zoom, bound to the mouse wheel. Value 16.
    /// Blocking it stops the camera zooming; it does not zero <c>Mouse.current.scroll</c>
    /// and it does not deliver the wheel to a uGUI <c>ScrollRect</c>. The shown-input
    /// pump reads that device on a different path.
    /// </summary>
    internal const int ZoomCamera = 16;

    /// <summary>
    /// Every analog action (<c>All = int.MaxValue</c>): move, aim, zoom,
    /// camera rotate and move, radial, map zoom and pan, menu sticks. Only the
    /// <see cref="UiMaskScope.Exclusive"/> scope takes it.
    /// </summary>
    internal const int All = int.MaxValue;
}

/// <summary>
/// How much of the input mask a shown panel is asking for this frame.
///
/// <para>
/// Before job FUI20 this was not a scale but a switch on the <i>registration</i>: the hover router
/// decided whether the context was in <c>InputActionSystem._InputContexts</c> at all, so a panel
/// the pointer was not over blocked nothing because there was nothing there to block with. That is
/// right for the wheel and the mouse buttons and wrong for exactly one key — see
/// <see cref="InputMaskPlan"/>.
/// </para>
/// </summary>
internal enum UiMaskScope
{
    /// <summary>
    /// The pointer is not over any shown panel. Gameplay input belongs to the game; only the
    /// key that closes the panel is taken.
    /// </summary>
    CloseKeyOnly = 0,

    /// <summary>
    /// The pointer is over a shown panel (or a panel asked for <see cref="UiCaptureMode.WhileShown"/>,
    /// or nothing could be read and the module failed closed). Chat's whole recipe applies.
    /// </summary>
    Full = 1,

    /// <summary>
    /// A framework mode owns the whole of gameplay input (photo mode, <see cref="UiInputCapture.AcquireExclusive"/>):
    /// <see cref="Full"/>, and camera rotation is <b>not</b> allowed back, every analog action is taken,
    /// and the menu-open hotkeys are taken in both button lists. UI navigation stays (uGUI keeps working).
    /// </summary>
    Exclusive = 2,
}

/// <summary>
/// What our <c>GetConsumedInputs</c> postfix writes into the caller's <c>BlockInputState</c> for a
/// given <see cref="UiMaskScope"/>. Game-free, so the rule that a panel's close key is taken in
/// <i>every</i> scope — the whole of defect 1 — is a tested property rather than a line of native
/// callback code nobody can exercise.
///
/// <para>
/// <b>The defect (job FUI20).</b> With the settings panel open, one Escape press closed the mod
/// panel <i>and</i> opened the vanilla system menu. Two independent paths saw the same press:
/// Bootstrap's <c>UiInputContext.Update</c> reads <c>Keyboard.current.escapeKey</c> directly and
/// hides every shown panel, and the game's own pipeline turned the press into
/// <c>ButtonInputAction.Back</c> and handed it to <c>MenuInputSystem</c>. Nothing stopped the
/// second.
/// </para>
///
/// <para>
/// <b>What the game actually does with Escape.</b> <c>ProjectM.MenuInputSystem</c>
/// is the client-world system that opens menus, and it has no
/// per-frame code of its own: its <c>OnUpdate</c> is the shared empty stub,
/// and its only real entry point is
/// <c>HandleInput(InputState)</c>, which nothing but
/// <c>InputActionSystem</c> calls — after it has subtracted every registered context's
/// <c>BlockInputState</c>. No <c>ProjectM</c> type reads the keyboard directly: every
/// <c>escapeKey</c> / <c>Key.Escape</c> / <c>KeyCode.Escape</c> in the game's metadata belongs to Unity or the
/// BCL. So Escape <i>can</i> be taken
/// cleanly on this build, and the context list is the only place it can be taken.
/// </para>
///
/// <para>
/// <b>Why <c>BlockAllKeys(false)</c> was never going to do it.</b> The parameter is named
/// <c>includeUINavigation</c> and defaults to <c>false</c>: the
/// default deliberately exempts <c>ButtonInputActionCollection.UINavigationActions</c>,
/// which is what keeps uGUI alive under a chat box and under our panel.
/// That array is filled by a static constructor whose body the metadata does not contain,
/// so whether <see cref="ButtonInputActions.Back"/>
/// is inside it cannot be read offline — and <c>BlockAllKeys(true)</c> is not the answer either,
/// because it takes the whole navigation set and the panel stops responding to its own widgets.
/// The plan therefore never depends on the answer: it calls <c>BlockAllKeys(false)</c> for the
/// bulk and then names the close actions explicitly, which is correct whichever way that
/// <c>.cctor</c> goes.
/// </para>
///
/// <para>
/// <b>Why the close key cannot be hover-scoped.</b> Since job FUI19 the input block follows the
/// pointer, and the context is <i>removed from the game's list</i> when the pointer is not over a
/// panel — so with the mouse anywhere else there is nothing registered to block anything. That is
/// right for the wheel, the mouse buttons and movement: a panel open in one corner must not take
/// the screen hostage. It is wrong for Escape, which has to close the panel from anywhere, because
/// a close gesture that only works while the pointer happens to be inside the frame is not a close
/// gesture. So the registration now follows the panel and the <i>mask</i> follows the pointer:
/// <see cref="UiMaskScope.CloseKeyOnly"/> is a registered context that consumes two actions and
/// nothing else. This is also strictly less work against the game's ordered list than before —
/// crossing a panel edge changes a flag rather than issuing an <c>AddInputContext</c> /
/// <c>RemoveInputContext</c> pair.
/// </para>
/// </summary>
internal readonly struct InputMaskPlan
{
    private InputMaskPlan(
        UiMaskScope scope,
        bool blockAllKeys,
        bool blockMoveAndAim,
        bool blockZoom,
        bool blockCameraRotationFallback,
        bool exclusive = false)
    {
        Scope = scope;
        BlockAllKeys = blockAllKeys;
        BlockMoveAndAim = blockMoveAndAim;
        BlockZoom = blockZoom;
        BlockCameraRotationFallback = blockCameraRotationFallback;
        AllowCameraRotationOnKeyboardAndMouse = !exclusive;
        BlockAllAnalog = exclusive;
        BlockMenuHotkeys = exclusive;
        BlockCameraRotation = exclusive;
    }

    /// <summary>Take every analog action (<see cref="AnalogInputActions.All"/>). <see cref="UiMaskScope.Exclusive"/> only.</summary>
    internal bool BlockAllAnalog { get; }

    /// <summary>
    /// Take the menu-open hotkeys (<see cref="MenuHotkeyGuard.BlockedActions"/>) in both button lists,
    /// whether or not a text field is focused. <see cref="UiMaskScope.Exclusive"/> only.
    /// </summary>
    internal bool BlockMenuHotkeys { get; }

    /// <summary>Block <see cref="ButtonInputActions.RotateCamera"/> explicitly. <see cref="UiMaskScope.Exclusive"/> only.</summary>
    internal bool BlockCameraRotation { get; }

    internal UiMaskScope Scope { get; }

    /// <summary>Call <c>BlockInputState.BlockAllKeys</c> at all this frame.</summary>
    internal bool BlockAllKeys { get; }

    /// <summary>
    /// The argument to <c>BlockAllKeys</c>. Always <c>false</c>, in every scope, and there is a
    /// test that says so: <c>true</c> takes <c>UINavigationActions</c> with it and a panel that
    /// blocks its own uGUI navigation cannot be used. The close actions are named individually
    /// instead — see <see cref="BlockedCloseActions"/>.
    /// </summary>
    internal bool IncludeUiNavigation => false;

    /// <summary>Chat's analog set: <c>MoveX | MoveY | AimX | AimY</c>.</summary>
    internal bool BlockMoveAndAim { get; }

    /// <summary>
    /// <c>ZoomCamera</c> (<see cref="AnalogInputActions.ZoomCamera"/>). Chat leaves the wheel
    /// alive so a player typing can still zoom. A scrolling panel cannot: if the analog stays
    /// available, the camera consumes the same notches the list needs (evidence 167). Only in
    /// <see cref="UiMaskScope.Full"/>. Blocking this analog does not deliver OnScroll and
    /// does not zero the mouse device; the shown-input pump is the list mover.
    /// </summary>
    internal bool BlockZoom { get; }

    /// <summary>
    /// Whether <c>RotateCameraBlocker._BlockRotateCamera</c> — the context object's own persistent
    /// field, which vanilla <c>GetConsumedInputs</c> turns into a <c>BlockKey(RotateCamera)</c> —
    /// should be set. It is the only behavior left on a build where the mask detour did not
    /// resolve, so it follows the scope rather than the registration: a panel the pointer is not
    /// over must not stop the camera turning, on any build.
    /// </summary>
    internal bool BlockCameraRotationFallback { get; }

    /// <summary>
    /// Allow <see cref="ButtonInputActions.RotateCamera"/> back on keyboard and mouse, and un-block
    /// the rotate analog axes, exactly as <c>ChatFocusedInputContext</c> does — a player with a
    /// panel open still wants to look around. Unconditional: in
    /// <see cref="UiMaskScope.CloseKeyOnly"/> nothing blocked it in the first place, and re-allowing
    /// an action that is not blocked is a no-op.
    /// </summary>
    internal bool AllowCameraRotationOnKeyboardAndMouse { get; }

    /// <summary>
    /// The close actions, once. Everything else in this file and in <c>GameInputBlocker.Mask</c>
    /// reads this array; see <see cref="BlockedCloseActions"/> for why that is the whole point.
    /// </summary>
    private static readonly int[] CloseActions =
    {
        ButtonInputActions.Back,
        ButtonInputActions.MenuClose,
    };

    /// <summary>
    /// The actions taken in <b>every</b> scope: <see cref="ButtonInputActions.Back"/> and
    /// <see cref="ButtonInputActions.MenuClose"/>. This is the fix for defect 1, and it is a
    /// property of the plan rather than of a scope so that a scope added later cannot quietly
    /// stop consuming the close key.
    ///
    /// <para>
    /// <b>And it is the list that executes</b> (job FUI27, R45 finding 28). Until FUI27 no
    /// production code read this: <c>GameInputBlocker.Mask</c> hard-coded two <c>BlockKey</c>
    /// calls, so the documented single source of truth and the native callback were two
    /// unconnected copies and a third action added here would simply not have been blocked. The
    /// only test that noticed was an <c>Assert.Equal(2, …Length)</c>, which fails for the length
    /// rather than for the behavior. <c>Mask</c> now iterates this array, and
    /// <c>UiInputMaskTests.The_native_mask_iterates_the_documented_close_action_list</c> is what
    /// keeps it doing so.
    /// </para>
    ///
    /// <para>
    /// <b>The returned array is the live list, not a copy</b> — deliberately, because the caller
    /// that matters is a native callback that runs once per frame per context and must not
    /// allocate there, and because "the documented list is the one that executes" is a claim about
    /// identity. Nothing may mutate it; <c>UiInputMaskTests</c> pins both the identity and the
    /// contents.
    /// </para>
    /// </summary>
    internal static int[] BlockedCloseActions() => CloseActions;

    /// <summary>
    /// True when <paramref name="action"/> is one of the close actions. Reads
    /// <see cref="BlockedCloseActions"/> rather than restating it, so this predicate and the mask
    /// cannot disagree about what a close action is (job FUI27).
    /// </summary>
    internal static bool IsCloseAction(int action)
    {
        for (int i = 0; i < CloseActions.Length; i++)
        {
            if (CloseActions[i] == action) return true;
        }

        return false;
    }

    /// <summary>
    /// The plan for a scope. An undefined <see cref="UiMaskScope"/> — a cast integer — is treated
    /// as <see cref="UiMaskScope.Full"/> rather than as "block nothing": this decides how much of
    /// the game a panel takes, and the fail-closed answer is the one that keeps a panel usable,
    /// the same rule <see cref="HoverRouter"/> follows for an unreadable pointer.
    /// </summary>
    internal static InputMaskPlan For(UiMaskScope scope)
    {
        if (scope == UiMaskScope.CloseKeyOnly)
        {
            return new InputMaskPlan(
                UiMaskScope.CloseKeyOnly,
                blockAllKeys: false,
                blockMoveAndAim: false,
                blockZoom: false,
                blockCameraRotationFallback: false);
        }

        if (scope == UiMaskScope.Exclusive)
        {
            return new InputMaskPlan(
                UiMaskScope.Exclusive,
                blockAllKeys: true,
                blockMoveAndAim: true,
                blockZoom: true,
                blockCameraRotationFallback: true,
                exclusive: true);
        }

        return new InputMaskPlan(
            UiMaskScope.Full,
            blockAllKeys: true,
            blockMoveAndAim: true,
            blockZoom: true,
            blockCameraRotationFallback: true);
    }

    /// <summary>The scope a pointer-gate answer asks for.</summary>
    internal static UiMaskScope ScopeFor(bool pointerBlocked) =>
        pointerBlocked ? UiMaskScope.Full : UiMaskScope.CloseKeyOnly;
}
