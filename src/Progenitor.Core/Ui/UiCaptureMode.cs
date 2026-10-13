namespace Progenitor.Ui;

/// <summary>
/// How an open mod panel treats gameplay input. The framework default is
/// <see cref="WhenHovered"/> (evidence 177).
///
/// <para>
/// <b>Two things used to be one.</b> "Capture" meant both <i>unlock the cursor</i> and <i>block
/// gameplay input</i>, and a panel did both for as long as it was shown. Only the second belongs
/// to the pointer: the cursor has to stay unlocked the whole time a panel is open, or the player
/// could not move the mouse to the panel in the first place. So the cursor follows the panel and
/// the input block follows the pointer, and this enum only ever decides the second.
/// </para>
///
/// <para>
/// <b>Public rather than internal</b> (the one change job FUI19 made to this file): the framework
/// default has to be overridable per plugin, and the call that does it —
/// <see cref="Ui.CaptureInput(UiCaptureMode, object?)"/> — lives on a public static class that a
/// plugin calls directly. An internal enum cannot appear in that signature. The default itself,
/// the config entry that moves it framework-wide, and the parse that reads it are in
/// <see cref="UiCaptureModes"/>; the decision it feeds is <see cref="HoverRouter"/>.
/// </para>
/// </summary>
public enum UiCaptureMode
{
    /// <summary>
    /// Gameplay input is blocked only while the pointer is over one of the plugin's shown
    /// panels. Over the panel, the wheel scrolls the list and a click hits a row; off the panel,
    /// the same wheel and the same click go to the game. This is the default because it is what a
    /// player expects from every other windowed game UI, and because the alternative makes a
    /// panel that happens to be open take the whole screen hostage.
    /// </summary>
    WhenHovered = 0,

    /// <summary>
    /// Gameplay input is blocked for as long as any panel is shown, wherever the pointer is. The
    /// behavior before evidence 177, kept for a panel that is genuinely modal — one that must
    /// not let the player walk away mid-edit, or that binds keys it cannot share.
    /// </summary>
    WhileShown = 1,

    /// <summary>
    /// Gameplay input is never blocked. For a HUD-style overlay the player reads while playing;
    /// what <c>Ui.CaptureInput(false)</c> has always meant.
    ///
    /// <para>
    /// <b>Escape closes this panel and also opens the vanilla system menu</b> (job FUI23, R44
    /// finding 6). The two are separate mechanisms: the cursor unlock and the Escape <i>forwarding</i>
    /// come from Bootstrap's capture refcount, which every shown panel drives whatever its mode is,
    /// while Escape <i>consumption</i> comes from a registered input context — and a
    /// <c>Never</c> panel deliberately registers none. That is the documented meaning of the mode
    /// rather than a defect (a HUD overlay you read while playing should not eat the ESC menu), but
    /// it is the one behavior a player will actually notice, so it is stated here instead of being
    /// left to be discovered. <see cref="WhenHovered"/> and <see cref="WhileShown"/> both consume
    /// the key in every scope; only this mode does not.
    /// </para>
    /// </summary>
    Never = 2,
}
