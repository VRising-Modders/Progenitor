namespace Progenitor.Ui;

/// <summary>
/// The seam between <see cref="UiInputCapture"/> (refcount, deferral, logging — game-free)
/// and the game's ordered input-context list. Production is <c>GameInputBlocker</c>: it
/// instantiates <c>ProjectM.RotateCameraBlocker</c> and pushes it with
/// <c>InputActionSystem.AddInputContext(context, world, </c><see cref="UiInputCapture.Order"/><c>)</c>.
/// Not the blocker's own <c>Register</c>, which hard-codes 600 — that is
/// <c>InputContextOrder.MenuInput</c>, where the system that opens the vanilla escape menu also
/// sits, and a tie there is what let one Escape do two things (job FUI20). Tests supply a fake.
///
/// <para>
/// <b>Never add or remove a context from inside an input callback.</b>
/// <c>InputActionSystem</c> sets <c>_DenyModificationsOfInputContexts</c> around every
/// <c>GetConsumedInputs</c> / <c>HandleInput</c> call and both mutators then
/// <c>Debug.LogError</c> and do nothing ("You can not add an InputContext during
/// GetConsumedInputs or HandleInput!"). <see cref="DenyModifications"/> is that flag;
/// <see cref="UiInputCapture"/> defers to the next frame tick whenever it is set.
/// </para>
/// </summary>
internal interface IUiInputBlocker
{
    /// <summary>True while our context object is in the system's <c>_InputContexts</c> list.</summary>
    bool IsRegistered { get; }

    /// <summary>
    /// The live <c>RotateCameraBlocker._BlockRotateCamera</c> of the context object we own.
    /// Unlike the per-frame mask this one is a <b>persistent field</b>: vanilla
    /// <c>GetConsumedInputs</c> is <c>if (_BlockRotateCamera) BlockKey(RotateCamera)</c>, so a
    /// context left in the list with the flag set blocks camera rotation for the rest of the
    /// process with no owner to clear it (R31 finding 8). <see cref="Remove"/> and
    /// <see cref="Dispose"/> must leave this <c>false</c> even when the removal was refused.
    /// </summary>
    bool BlocksCameraRotation { get; }

    /// <summary>
    /// The live <c>InputActionSystem._DenyModificationsOfInputContexts</c>. True means a
    /// context callback is on the stack and add/remove would be refused. Reading it must
    /// never throw; an unreachable system returns false.
    /// </summary>
    bool DenyModifications { get; }

    /// <summary>
    /// Push our context at <see cref="UiInputCapture.Order"/>. False when the client world or
    /// the input system is not reachable (main menu, server process) — the caller logs and
    /// leaves the refcount alone so a later frame can retry.
    ///
    /// <para>
    /// <b>Must verify the add landed</b> (<c>IsContextRegistered</c>), not just that nothing
    /// threw. <c>RotateCameraBlocker.Register</c> sets its own <c>_Registered = true</c>
    /// unconditionally, while <c>AddInputContext</c> under the deny flag logs and returns
    /// without adding, so an unverified refusal poisons the context object for the process:
    /// every later <c>Register</c> is a no-op behind that instance's own guard (R31 finding 4).
    /// A refused add must therefore also drop the cached instance and report <c>false</c>.
    /// </para>
    /// </summary>
    bool Register();

    /// <summary>
    /// Pop our context. False when the removal did not take (caller retries next frame).
    /// A refused removal must leave the still-listed entry inert:
    /// <see cref="BlocksCameraRotation"/> is <c>false</c> afterwards either way.
    /// </summary>
    bool Remove();

    /// <summary>
    /// How much the registered context should mask from now on (job FUI20). Driven by the pointer
    /// gate, and <b>not</b> by whether anything is registered: the scope is a property of the
    /// blocker, applied by whatever context is or later becomes live.
    ///
    /// <para>
    /// Called with no lock held and possibly while nothing is registered. Must never throw, and
    /// must be cheap enough for a pointer crossing a panel edge — it is not, and must not become,
    /// a list mutation. See <see cref="InputMaskPlan"/> for what each scope means and why the
    /// close key is taken in all of them.
    /// </para>
    /// </summary>
    void SetMaskScope(UiMaskScope scope);

    /// <summary>
    /// The scope this blocker is <b>actually</b> masking at — the value its per-frame mask reads,
    /// not the value the module last decided (job FUI23, R44 findings 1 and 4).
    ///
    /// <para>
    /// <b>Why the interface carries it.</b> There were two copies of the pointer answer:
    /// <c>UiInputCapture._pointerBlock</c> and <c>GameInputBlocker._scope</c>, kept in step by one
    /// mechanism that one caller wrote behind. Nothing could see them disagree, because the only
    /// diagnostic aimed at the mask (<see cref="UiInputCapture.MaskScope"/>,
    /// <c>Ui.DescribePins</c>) reported the copy that by construction always agrees with itself.
    /// This is the other copy, so the diagnostic reports a measurement instead of an intention and
    /// <see cref="UiInputCapture.SetPointerBlock"/> can decide whether a push is needed from the
    /// blocker's real state rather than from its own shadow of it.
    /// </para>
    ///
    /// <para>
    /// Read with no lock held, possibly from a property getter that must not block: implementations
    /// answer from a field, never from a game call, and must never throw. It says what <i>would</i>
    /// be masked, not whether anything is registered to mask it — <see cref="IsRegistered"/> is
    /// that question.
    /// </para>
    /// </summary>
    UiMaskScope AppliedScope { get; }

    /// <summary>
    /// Whether the mask this blocker documents is actually installed (job FUI23, R44 finding 3).
    /// <c>false</c> means the <c>GetConsumedInputs</c> detour did not resolve or did not apply on
    /// this build, so a registered context blocks camera rotation and <b>nothing else</b> — in
    /// particular <c>Back</c> / <c>Menu_Close</c> are not consumed, and one Escape press closes the
    /// panel and opens the vanilla system menu, which is job FUI20's defect 1 unfixed.
    ///
    /// <para>
    /// It exists so <see cref="UiInputCapture.RegisteredLog"/> reports the mask that was installed
    /// rather than the recipe it intended to install. Read outside every lock, from a field, and
    /// never throws.
    /// </para>
    /// </summary>
    bool MaskLive { get; }

    /// <summary>
    /// Drop everything this blocker owns: the native detour on <c>GetConsumedInputs</c> and the
    /// context object itself. Called from Core Uninstall after <see cref="Remove"/>.
    /// </summary>
    void Dispose();
}

/// <summary>Server process, or Ui skipped: nothing to register, nothing to defer.</summary>
internal sealed class NullUiInputBlocker : IUiInputBlocker
{
    public static readonly NullUiInputBlocker Instance = new();

    private volatile int _scope = (int)UiMaskScope.Full;

    public bool IsRegistered => false;

    public bool BlocksCameraRotation => false;

    public bool DenyModifications => false;

    /// <summary>
    /// The scope it was last told, echoed back. Nothing is registered here so nothing is masked at
    /// all — <see cref="IsRegistered"/> is what says that, and <c>Ui.DescribePins</c> prints both —
    /// but the echo is what keeps <see cref="IUiInputBlocker.AppliedScope"/> a real measurement of
    /// this blocker rather than a constant that would make every push look redundant.
    /// </summary>
    public UiMaskScope AppliedScope => (UiMaskScope)_scope;

    /// <summary>No detour, so no mask: a server or a skipped Ui consumes nothing.</summary>
    public bool MaskLive => false;

    public bool Register() => false;

    public bool Remove() => true;

    public void SetMaskScope(UiMaskScope scope) => _scope = (int)scope;

    public void Dispose() => _scope = (int)UiMaskScope.Full;
}
