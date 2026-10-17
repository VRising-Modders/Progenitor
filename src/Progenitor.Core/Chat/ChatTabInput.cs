using System;

namespace Progenitor.Chat;

/// <summary>
/// CHAT13: where a tab's typed line goes, as the game's <c>ChatMessageType</c> number (Global 0, Team 2, Whisper 3,
/// Local 4, System 5). <see cref="Whisper"/> on a tab means "whisper the latest whisper partner".
/// </summary>
internal enum ChatSend
{
    Global = 0,
    Clan = 2,
    Whisper = 3,
    Local = 4,
    System = 5,
}

/// <summary>
/// CHAT13: the input line per tab. Game-free; the live half is <see cref="ChatTabs"/> (tab switches) and
/// <c>ChatWindow</c> (each frame).
///
/// <list type="bullet">
/// <item><b>Send channel per tab.</b> Each tab remembers the channel last chosen while it was shown
/// (<see cref="ChatTab.SendTo"/>), saved with the tab. Until then it uses <see cref="DefaultFor"/>: the narrowest
/// audience the tab shows (Clan, then Local, then Global), so a mixed tab never defaults to the whole server; a tab
/// that shows whispers and no other sendable channel whispers the latest partner; All, Mentions and System tabs keep
/// whatever channel is in use. Showing a tab switches the game's channel to the tab's.</item>
/// <item><b>Explicit choices win.</b> A channel the player picks while typing (<c>/g</c>, <c>/l</c>, <c>/c</c>, the
/// game's Tab cycle) becomes the shown tab's channel (<see cref="Remember"/>). A whisper is never remembered as a
/// tab's channel (whispers are not sticky in the popular chats); on a whisper tab it brings back the tab's default.</item>
/// <item><b>Whisper tab.</b> Opening the input on a whisper tab with an empty line puts <c>/w partner </c> in it,
/// once per opening, the partner being <c>/r</c>'s (<see cref="ChatWhisperMemory.Partner"/>). With no partner yet
/// the tab falls back to its first other channel, else keeps the current one.</item>
/// <item><b>Draft per tab.</b> Unsent text stays with the tab it was typed in and comes back when that tab is shown
/// again (<see cref="SwapDraft"/>). Drafts are kept for the session only: a half-typed game chat line is stale by the
/// next session, and the sent-line recall (Up) already keeps what was sent.</item>
/// </list>
/// </summary>
internal static class ChatTabInput
{
    /// <summary>The tab's channel before the player chose one: null keeps the channel in use.</summary>
    internal static ChatSend? DefaultFor(ChatTab tab)
    {
        if (tab == null || tab.Fixed) return null;
        return FirstChannel(tab.Scope) ?? ((tab.Scope & ChatScope.Whispers) != 0 ? ChatSend.Whisper : null);
    }

    /// <summary>The narrowest channel the scope shows that a line can be typed to: Clan, Local, then Global.</summary>
    internal static ChatSend? FirstChannel(ChatScope scope)
    {
        if ((scope & ChatScope.Clan) != 0) return ChatSend.Clan;
        if ((scope & ChatScope.Local) != 0) return ChatSend.Local;
        if ((scope & ChatScope.Global) != 0) return ChatSend.Global;
        return null;
    }

    /// <summary>
    /// Where typing in <paramref name="tab"/> goes now: the remembered channel, else the default. A whisper with no
    /// partner yet falls back to the tab's first other channel, or null (keep the channel in use).
    /// </summary>
    internal static ChatSend? EffectiveFor(ChatTab tab, string? partner)
    {
        if (tab == null) return null;
        var send = tab.SendTo ?? DefaultFor(tab);
        if (send == ChatSend.Whisper && string.IsNullOrWhiteSpace(partner)) return FirstChannel(tab.Scope);
        return send;
    }

    /// <summary>The game channel number to switch to when the tab is shown, or -1 (leave it; whisper is a prefill).</summary>
    internal static int ModeFor(ChatTab tab, string? partner)
    {
        var send = EffectiveFor(tab, partner);
        return send == null || send == ChatSend.Whisper ? -1 : (int)send.Value;
    }

    /// <summary>The line an empty input gets on this tab: <c>/w partner </c> for a whisper tab, else null.</summary>
    internal static string? PrefillFor(ChatTab tab, string? partner) =>
        EffectiveFor(tab, partner) == ChatSend.Whisper ? "/w " + partner!.Trim() + " " : null;

    /// <summary>Whether <paramref name="text"/> is only a prefill (nothing typed after <c>/w name </c>).</summary>
    internal static bool IsOnlyPrefill(string? text, string? prefill) =>
        !string.IsNullOrEmpty(prefill) && string.Equals((text ?? "").Trim(), prefill!.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The game's channel moved to <paramref name="mode"/> while the player typed on <paramref name="tab"/>: remember it
    /// as the tab's (a value equal to the default is stored as none, so editing the tab's channels later re-derives it).
    /// Region, unknown modes and whispers are not remembered (a whisper on a whisper tab is its default again). True
    /// when it changed.
    /// </summary>
    internal static bool Remember(ChatTab tab, int mode)
    {
        if (tab == null || !Enum.IsDefined(typeof(ChatSend), mode)) return false;
        var send = (ChatSend)mode;
        if (send == ChatSend.Whisper && DefaultFor(tab) != ChatSend.Whisper) return false;
        ChatSend? stored = send == DefaultFor(tab) ? null : send;
        if (tab.SendTo == stored) return false;
        tab.SendTo = stored;
        return true;
    }

    /// <summary>
    /// Move the line between tabs: the leaving tab keeps <paramref name="current"/> as its draft (not a bare prefill),
    /// and the line becomes the arriving tab's draft. Returns the new line. With no leaving tab (the first tab shown in
    /// a world) or the same tab, the line stays as it is.
    /// </summary>
    internal static string SwapDraft(ChatTab? from, ChatTab to, string? current, string? fromPrefill)
    {
        if (from == null || ReferenceEquals(from, to)) return current ?? "";
        from.Draft = IsOnlyPrefill(current, fromPrefill) ? "" : current ?? "";
        return to.Draft ?? "";
    }
}

/// <summary>
/// CHAT13: follows the game's channel each frame to learn the player's explicit choices, and arms the whisper prefill
/// once per opening of the input. Game-free.
/// </summary>
internal sealed class ChatTabInputTracker
{
    private int _lastMode = -1;
    private bool _prefilled;

    /// <summary>The channel Progenitor just set (a tab switch): not the player's choice.</summary>
    internal void Applied(int mode)
    {
        if (mode >= 0) _lastMode = mode;
    }

    /// <summary>A tab switch: the next empty focused line may get the new tab's prefill.</summary>
    internal void TabChanged() => _prefilled = false;

    internal void Reset()
    {
        _lastMode = -1;
        _prefilled = false;
    }

    /// <summary>
    /// One frame. A channel change seen while the player types is remembered on <paramref name="tab"/> (returns true:
    /// save the tabs); a change while the input is closed (a world load) is only noted.
    /// </summary>
    internal bool Observe(ChatTab? tab, int mode, bool focused)
    {
        bool changed = false;
        if (focused && _lastMode >= 0 && mode != _lastMode && tab != null) changed = ChatTabInput.Remember(tab, mode);
        _lastMode = mode;
        if (!focused) _prefilled = false;
        return changed;
    }

    /// <summary>
    /// The line to put in the input this frame, or null: the tab's whisper prefill, once per opening (or tab switch),
    /// while the input is open and empty.
    /// </summary>
    internal string? Prefill(ChatTab? tab, string? partner, bool focused, string? text)
    {
        if (!focused || _prefilled || tab == null) return null;
        if (!string.IsNullOrEmpty(text))
        {
            // Something is already typed: this opening never gets a prefill.
            _prefilled = true;
            return null;
        }

        _prefilled = true;
        return ChatTabInput.PrefillFor(tab, partner);
    }
}
