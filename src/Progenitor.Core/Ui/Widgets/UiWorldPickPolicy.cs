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

    /// <summary>PICKMOD1: the token in <see cref="UiWorldPickOptions.ModifierHint"/> that stands for the game's Interact key.</summary>
    internal const string InteractToken = "{interact}";

    /// <summary>
    /// PICKMOD1: the band's second line from <see cref="UiWorldPickOptions.ModifierHint"/>: <c>"Shift+{interact}: except"</c>
    /// is the key cap <c>Shift+F</c> (the game's Interact key put in for the token, any case) followed by the words
    /// <c>except</c>. A hint without <c>": "</c> is words only (no cap). Empty for no hint. Each part trimmed.
    /// </summary>
    internal static (string Keys, string Words) SplitHint(string? hint, string interactKey)
    {
        if (string.IsNullOrWhiteSpace(hint)) return ("", "");
        string text = hint!.Trim();
        int colon = text.IndexOf(": ", StringComparison.Ordinal);
        string keys = colon > 0 ? text.Substring(0, colon).Trim() : "";
        string words = colon > 0 ? text.Substring(colon + 2).Trim() : text;
        if (keys.Length > 0)
        {
            string key = string.IsNullOrWhiteSpace(interactKey) ? InteractFallback : interactKey.Trim();
            int at;
            while ((at = keys.IndexOf(InteractToken, StringComparison.OrdinalIgnoreCase)) >= 0)
                keys = keys.Substring(0, at) + key + keys.Substring(at + InteractToken.Length);
        }
        return (keys, words);
    }
}

/// <summary>
/// PICKMOD1, game-free: which modifier keys count as held at a pick's Interact press. The framework samples its own held
/// modifiers once a frame while a pick is armed; the interact is seen a frame or more after the press, by which time a
/// quick player may have let go of Shift, so the modifiers held at any sample in the last <see cref="WindowSeconds"/>
/// count.
/// </summary>
internal sealed class UiWorldPickModifiers
{
    /// <summary>How far back a held modifier still counts for the next pick.</summary>
    internal const double WindowSeconds = 0.25;

    private readonly System.Collections.Generic.List<(double At, KeyModifiers Held)> _samples = new();

    /// <summary>
    /// After a delivery: the modifiers of the press just delivered may still be held. Samples count again only from a
    /// frame with none held, so a Shift let go slowly after one pick never reaches the next.
    /// </summary>
    private bool _awaitRelease;

    /// <summary>One frame's held modifiers.</summary>
    internal void Note(double now, KeyModifiers held)
    {
        Prune(now);
        if (_awaitRelease)
        {
            if (held != KeyModifiers.None) return;
            _awaitRelease = false;
        }
        _samples.Add((now, held));
    }

    /// <summary>Every modifier held at a sample within <see cref="WindowSeconds"/> of <paramref name="now"/>.</summary>
    internal KeyModifiers At(double now)
    {
        Prune(now);
        var all = KeyModifiers.None;
        foreach (var s in _samples) all |= s.Held;
        return all;
    }

    /// <summary>
    /// A pick's Interact press is seen: note <paramref name="heldNow"/> (read at that moment), take the modifiers that
    /// count (<see cref="At"/>), then <see cref="Delivered"/>.
    /// </summary>
    internal KeyModifiers Deliver(double now, KeyModifiers heldNow)
    {
        Note(now, heldNow);
        var held = At(now);
        Delivered();
        return held;
    }

    /// <summary>A pick armed: start fresh (a modifier already held counts).</summary>
    internal void Clear()
    {
        _samples.Clear();
        _awaitRelease = false;
    }

    /// <summary>
    /// A pick delivered: a repeating pick's next press starts fresh, and only from a frame with no modifier held (one
    /// still held from this press is not the next press's). To pick again with Shift, let go and press it again.
    /// </summary>
    internal void Delivered()
    {
        _samples.Clear();
        _awaitRelease = true;
    }

    private void Prune(double now)
    {
        int keep = 0;
        while (keep < _samples.Count && now - _samples[keep].At > WindowSeconds) keep++;
        if (keep > 0) _samples.RemoveRange(0, keep);
    }
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
/// <para><b>The band takes the close key and nothing else.</b> The band is a <see cref="UiCaptureMode.Never"/>
/// panel (<see cref="BandMode"/>): the pointer never routes it, so it never asks for the full mask (a
/// <c>WhenHovered</c> band failed closed to it with an unreadable pointer, a controller player's case). While the
/// pick is topmost (<see cref="BandHoldsCloseKey"/>) it holds a close-key-only context
/// (<c>UiInputCapture.AcquireCloseKey</c>): the game's Back and Menu_Close are taken, so Escape cancels the pick
/// without opening the system menu, and movement, aim, zoom and every other key stay the game's. Otherwise it holds
/// nothing, so no context of ours stands between the game and its own menu's close. A <see cref="UiCaptureMode.Never"/>
/// panel still keeps Bootstrap forwarding Escape to the framework.</para>
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

    /// <summary>The band's capture mode: always <see cref="UiCaptureMode.Never"/> (it never asks for a gameplay block).</summary>
    internal static UiCaptureMode BandMode(UiWorldPickEscapeOwner owner) => UiCaptureMode.Never;

    /// <summary>Whether the band holds a close-key-only context: only while the pick owns Escape.</summary>
    internal static bool BandHoldsCloseKey(UiWorldPickEscapeOwner owner) => owner == UiWorldPickEscapeOwner.Pick;

    /// <summary>The mask the band's hold asks for: the close key only, wherever the pointer is.</summary>
    internal static UiMaskScope BandScope => UiMaskScope.CloseKeyOnly;

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

/// <summary>
/// The pick band's look as numbers (the framework notice band: a dark band between thin gold rules, both fading to clear
/// at the ends), game-free so the geometry and the fade are tested without a client. One-shot and repeating picks share it.
/// </summary>
internal static class UiWorldPickBandLook
{
    /// <summary>The band's least width in canvas units; it grows past it only for a very long prompt.</summary>
    internal const float MinWidth = 980f;

    internal const float Height = 52f;

    /// <summary>PICKMOD1: what a modifier hint's second line adds to the band's height.</summary>
    internal const float HintRowHeight = 36f;

    /// <summary>Padding between the band's content and its ends (inside the fade).</summary>
    internal const float PadX = 90f;

    /// <summary>The top and bottom rules' thickness.</summary>
    internal const float RuleHeight = 1f;

    /// <summary>The ground's opacity in the middle (black).</summary>
    internal const float GroundAlpha = 0.84f;

    /// <summary>The share of the width, at each end, over which the ground and the rules fade to clear.</summary>
    internal const float FadeShare = 0.16f;

    /// <summary>The fade texture's width in pixels (one row, stretched; bilinear).</summary>
    internal const int FadeTextureWidth = 256;

    /// <summary>The band's width for content <paramref name="contentWidth"/> wide.</summary>
    internal static float Width(float contentWidth) =>
        float.IsNaN(contentWidth) || contentWidth < 0f ? MinWidth : Math.Max(MinWidth, (float)Math.Ceiling(contentWidth + 2f * PadX));

    /// <summary>The opacity at <paramref name="t"/> across the band (0 the left end, 1 the right): 1 in the middle, easing to 0 at both ends.</summary>
    internal static float Fade(float t)
    {
        if (float.IsNaN(t)) return 0f;
        float edge = Math.Min(t, 1f - t);
        if (edge <= 0f) return 0f;
        if (edge >= FadeShare) return 1f;
        float u = edge / FadeShare;
        return u * u * (3f - 2f * u); // smoothstep
    }

    /// <summary>The fade texture's coverage at pixel <paramref name="x"/>.</summary>
    internal static float FadeAlpha(int x) => Fade((x + 0.5f) / FadeTextureWidth);
}
