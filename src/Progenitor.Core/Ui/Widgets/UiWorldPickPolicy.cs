using System;

namespace Progenitor.Ui;

/// <summary>The framework's own words in the pick prompt band, in one place (sentence case).</summary>
internal static class UiWorldPickCopy
{
    /// <summary>The panel title (never drawn; the band hides it, but logs and readbacks name it).</summary>
    internal const string BandTitle = "Pick in world";

    /// <summary>The word after the Interact key cap.</summary>
    internal const string PickWord = "Pick";

    /// <summary>The word after the Escape key cap.</summary>
    internal const string CancelWord = "Cancel";

    /// <summary>P7b: the word after the Escape key cap while a repeating pick runs (Escape ends it as finished).</summary>
    internal const string FinishWord = "Finish";

    /// <summary>P7b: the word after the Interact key cap while a repeating pick runs (each interact adds a target).</summary>
    internal const string AddWord = "Add";

    /// <summary>P7b: what separates the prompt from a repeating pick's count on the band (drawn muted).</summary>
    internal const string CountSeparator = "·";

    /// <summary>
    /// P7b: a repeating pick's count on the band, drawn in the gold after the prompt (<c>3 picked</c>); empty for a
    /// one-shot pick or before the first pick.
    /// </summary>
    internal static string BandCount(bool repeat, int count) =>
        !repeat || count <= 0 ? "" : count.ToString(System.Globalization.CultureInfo.InvariantCulture) + " picked";

    /// <summary>P7b: the word after the Interact key cap for a pick.</summary>
    internal static string InteractWord(bool repeat) => repeat ? AddWord : PickWord;

    /// <summary>P7b: the word after the Escape key cap for a pick.</summary>
    internal static string EscapeWord(bool repeat) => repeat ? FinishWord : CancelWord;

    /// <summary>
    /// P7b: the band's bottom edge, in canvas units above the screen's bottom edge. The HUD's health readout and the buff
    /// row above the action bar reach about 180 units up on the 1920x1080 design canvas (measured on a capture); the band
    /// stays clear of them with its bottom at least <see cref="MinBandBottom"/> up.
    /// </summary>
    internal const float BandBottom = 200f;

    /// <summary>P7b: the lowest the band's bottom edge may sit (the HUD clearance the mockup review found).</summary>
    internal const float MinBandBottom = 196f;

    /// <summary>The Interact key cap's text when the game's binding cannot be read.</summary>
    internal const string InteractFallback = "Interact";

    /// <summary>The Escape key cap's text (the game's own Controls page spelling).</summary>
    internal const string EscapeKey = "Esc";

    /// <summary>Widest the prompt text may draw, in reference pixels, before it ends in an ellipsis.</summary>
    internal const float MaxPromptWidth = 900f;
}

/// <summary>Who takes an Escape press while a world pick is armed.</summary>
internal enum UiWorldPickEscapeOwner
{
    /// <summary>No pick is armed: the pick has no say.</summary>
    NoPick = 0,

    /// <summary>A framework dialog or an open dropdown list answers it.</summary>
    Dialog = 1,

    /// <summary>A waiting key capture (a rebind) cancels.</summary>
    KeyCapture = 2,

    /// <summary>The game's chat box has focus and closes.</summary>
    Chat = 3,

    /// <summary>Another Progenitor panel is shown (the Mods workspace, a mod's panel): the usual close runs.</summary>
    OpenPanels = 4,

    /// <summary>A game menu or panel is open (character menu, a station panel, the fullscreen menu): the game closes it.</summary>
    GameMenu = 5,

    /// <summary>The pick is the topmost thing: Escape cancels it.</summary>
    Pick = 6,
}

/// <summary>
/// The Escape precedence for a world pick, and the band's capture mode that follows from it.
///
/// <para><b>Precedence</b>, first match wins: a framework dialog (or open dropdown) &gt; a key capture
/// &gt; the game's focused chat box &gt; any other shown Progenitor panel &gt; an open game menu or panel
/// &gt; the armed pick. Only when nothing above the pick is open does Escape cancel it; otherwise the
/// press does what it would do with no pick armed and the pick stays armed.</para>
///
/// <para><b>Why the band's capture mode follows it.</b> A shown panel with capture
/// (<see cref="UiCaptureMode.WhenHovered"/>/<see cref="UiCaptureMode.WhileShown"/>) registers the input
/// context that takes the game's Back and Menu_Close in every scope, so while it is registered a game
/// menu could not be closed with Escape. The band therefore asks for capture only while the pick is
/// topmost (<see cref="BandMode"/>), so its context exists exactly when Escape is the pick's. A
/// <see cref="UiCaptureMode.Never"/> panel still keeps Bootstrap forwarding Escape to the framework.</para>
/// </summary>
internal static class UiWorldPickEscape
{
    /// <summary>Who takes the press. <paramref name="otherPanelsShown"/> excludes the band itself.</summary>
    internal static UiWorldPickEscapeOwner Decide(
        bool pickArmed, bool dialogOpen, bool captureOwnsEscape, bool chatFocused, int otherPanelsShown, bool gameMenuOpen)
    {
        if (!pickArmed) return UiWorldPickEscapeOwner.NoPick;
        if (dialogOpen) return UiWorldPickEscapeOwner.Dialog;
        if (captureOwnsEscape) return UiWorldPickEscapeOwner.KeyCapture;
        if (chatFocused) return UiWorldPickEscapeOwner.Chat;
        if (otherPanelsShown > 0) return UiWorldPickEscapeOwner.OpenPanels;
        if (gameMenuOpen) return UiWorldPickEscapeOwner.GameMenu;
        return UiWorldPickEscapeOwner.Pick;
    }

    /// <summary>The band's capture mode: capture (for Escape) only while the pick owns Escape.</summary>
    internal static UiCaptureMode BandMode(UiWorldPickEscapeOwner owner) =>
        owner == UiWorldPickEscapeOwner.Pick ? UiCaptureMode.WhenHovered : UiCaptureMode.Never;

    /// <summary>
    /// The game's open HUD menu (<c>OpenHUDMenuSystem.CurrentMenuType</c>) counts as open for any value
    /// above 0 (<c>HUDMenuType.None</c>); -1 means the system could not be read and counts as closed.
    /// </summary>
    internal static bool GameMenuOpen(int currentMenuType) => currentMenuType > 0;
}

/// <summary>What the client does with one wanted interact.</summary>
internal readonly struct UiWorldPickSuppression
{
    private UiWorldPickSuppression(bool suppressed, bool swallow)
    {
        Suppressed = suppressed;
        Swallow = swallow;
    }

    /// <summary>The panel is kept closed (the event and init entities are destroyed).</summary>
    internal bool Suppressed { get; }

    /// <summary>Repeats for the same interact buff are kept closed until the server removes it.</summary>
    internal bool Swallow { get; }

    /// <summary>
    /// Decide for an interact the armed pick takes. The panel is kept closed only when the pick asks
    /// for it, the interact buff is known, and the stop message for it was actually sent
    /// (<paramref name="trySendStop"/>, called only then): a closed panel with the server still
    /// believing the player is interacting would leave the target in use and the panel would pop open
    /// on the HUD's next request. In every other case the panel opens and the result says
    /// <c>Suppressed = false</c>.
    /// </summary>
    internal static UiWorldPickSuppression Decide(UiWorldPickOffer offer, bool buffKnown, Func<bool> trySendStop)
    {
        if (offer != UiWorldPickOffer.TakeAndSuppress || !buffKnown) return new UiWorldPickSuppression(false, false);
        bool sent;
        try { sent = trySendStop(); }
        catch { sent = false; }
        return sent ? new UiWorldPickSuppression(true, true) : new UiWorldPickSuppression(false, false);
    }
}
