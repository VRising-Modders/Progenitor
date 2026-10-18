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
/// CHAT13: the input line per tab. Game-free; the live half is <see cref="ChatTabs"/> (tab switches, each frame).
///
/// <list type="bullet">
/// <item><b>Send channel per tab.</b> A tab that shows a channel a line can be typed to has a default
/// (<see cref="DefaultFor"/>): the narrowest audience it shows (Clan, then Local, then Global), so a mixed tab never
/// defaults to the whole server; a tab that shows whispers and no other such channel whispers the latest partner.
/// Showing the tab, and opening the input on it, switches the game's channel to the tab's.</item>
/// <item><b>Picks.</b> A channel the player picks while typing (<c>/g</c>, <c>/l</c>, <c>/c</c>, the game's Tab
/// cycle) is remembered as the tab's only when the tab shows that channel (<see cref="CanRemember"/>); any other pick
/// lasts for that opening of the input. All, Mentions-only and System-only tabs have no default and never remember a
/// pick: they keep the channel in use. Whispers are never remembered (not sticky in the popular chats).</item>
/// <item><b>Whisper tab.</b> Opening the input on a whisper tab with an empty line puts <c>/w partner </c> in it, once
/// per opening, the partner being <c>/r</c>'s (<see cref="ChatWhisperMemory.Partner"/>). Under that line, and with no
/// partner yet, the channel is the tab's first other channel, else Local (<see cref="WhisperFallback"/>): never
/// Global by default.</item>
/// <item><b>Draft per tab.</b> Unsent text stays with the tab it was typed in and comes back when that tab is shown
/// again (<see cref="SwapDraft"/>); a bare <c>/w name</c> line is never a draft. Drafts are kept for the session
/// only: a half-typed game chat line is stale by the next session, and the sent-line recall (Up) keeps what was
/// sent.</item>
/// </list>
/// </summary>
internal static class ChatTabInput
{
    /// <summary>The tab's channel before the player chose one: null keeps the channel in use (and never remembers).</summary>
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
    /// The channel under a whisper tab's line (a cleared line, or no partner yet): the tab's first other channel, else
    /// Local. Clan would be the better default for a player in a clan, but the client's own clan is not read here.
    /// </summary>
    internal static ChatSend WhisperFallback(ChatTab tab) => FirstChannel(tab.Scope) ?? ChatSend.Local;

    /// <summary>
    /// Whether a pick of <paramref name="send"/> may be remembered on <paramref name="tab"/>: the tab has a default and
    /// shows that channel (System only on a tab that shows system lines); a whisper only on a whisper tab, where it is
    /// the default again.
    /// </summary>
    internal static bool CanRemember(ChatTab tab, ChatSend send)
    {
        var def = DefaultFor(tab);
        if (def == null) return false;
        return send switch
        {
            ChatSend.Global => (tab.Scope & ChatScope.Global) != 0,
            ChatSend.Local => (tab.Scope & ChatScope.Local) != 0,
            ChatSend.Clan => (tab.Scope & ChatScope.Clan) != 0,
            ChatSend.System => (tab.Scope & ChatScope.SystemAndMods) != 0,
            ChatSend.Whisper => def == ChatSend.Whisper,
            _ => false,
        };
    }

    /// <summary>Where typing in <paramref name="tab"/> goes now: the remembered channel, else the default, or null.</summary>
    internal static ChatSend? EffectiveFor(ChatTab tab, string? partner)
    {
        if (tab == null) return null;
        var send = tab.SendTo ?? DefaultFor(tab);
        if (send == ChatSend.Whisper && string.IsNullOrWhiteSpace(partner)) return WhisperFallback(tab);
        return send;
    }

    /// <summary>
    /// The game channel number to switch to when the tab is shown or the input opens on it, or -1 (keep the channel
    /// in use). A whisper tab's whisper is a prefilled line over <see cref="WhisperFallback"/>.
    /// </summary>
    internal static int ModeFor(ChatTab tab, string? partner)
    {
        var send = EffectiveFor(tab, partner);
        if (send == null) return -1;
        return send == ChatSend.Whisper ? (int)WhisperFallback(tab) : (int)send.Value;
    }

    /// <summary>The line an empty input gets on this tab: <c>/w partner </c> for a whisper tab, else null.</summary>
    internal static string? PrefillFor(ChatTab tab, string? partner) =>
        EffectiveFor(tab, partner) == ChatSend.Whisper ? "/w " + partner!.Trim() + " " : null;

    /// <summary>
    /// A whisper command with a name and nothing after it (<c>/w Bob</c>, <c>/whisper Bob </c>): a prefill, the
    /// right-click Whisper line, or a stale one from an earlier partner. Never a draft, never recalled as sent.
    /// </summary>
    internal static bool IsBareWhisper(string? text)
    {
        string t = (text ?? "").Trim();
        foreach (var c in new[] { "/whisper ", "/w " })
        {
            if (!t.StartsWith(c, StringComparison.OrdinalIgnoreCase)) continue;
            string rest = t.Substring(c.Length).Trim();
            return rest.Length > 0 && rest.IndexOf(' ') < 0;
        }

        return false;
    }

    /// <summary>
    /// The line after a pending insert (right-click Whisper, Share in chat) lands in <paramref name="current"/>: a
    /// whisper insert replaces a bare whisper line (so a prefilled <c>/w Bob </c> never becomes
    /// <c>/w Bob /w Carol</c>); anything else is appended, with a space when needed.
    /// </summary>
    internal static string JoinInsert(string? current, string insert)
    {
        string now = current ?? "";
        if (IsBareWhisper(now) && IsWhisperCommand(insert)) return insert;
        return now.Length == 0 || now.EndsWith(" ", StringComparison.Ordinal) ? now + insert : now + " " + insert;
    }

    private static bool IsWhisperCommand(string? text) =>
        text != null && (text.StartsWith("/w ", StringComparison.OrdinalIgnoreCase) || text.StartsWith("/whisper ", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// The game's channel moved to <paramref name="mode"/> while the player typed on <paramref name="tab"/>: remember it
    /// as the tab's when <see cref="CanRemember"/> allows (a value equal to the default is stored as none, so editing
    /// the tab's channels later re-derives it). True when the stored value changed.
    /// </summary>
    internal static bool Remember(ChatTab tab, int mode)
    {
        if (tab == null || !Enum.IsDefined(typeof(ChatSend), mode)) return false;
        var send = (ChatSend)mode;
        if (!CanRemember(tab, send)) return false;
        ChatSend? stored = send == DefaultFor(tab) ? null : send;
        if (tab.SendTo == stored) return false;
        tab.SendTo = stored;
        return true;
    }

    /// <summary>
    /// Move the line between tabs: the leaving tab keeps <paramref name="current"/> as its draft (not a bare whisper
    /// line), and the line becomes the arriving tab's draft. Returns the new line. With no leaving tab (the first tab
    /// shown in a world) or the same tab, the line stays as it is.
    /// </summary>
    internal static string SwapDraft(ChatTab? from, ChatTab to, string? current)
    {
        if (from == null || ReferenceEquals(from, to)) return current ?? "";
        from.Draft = IsBareWhisper(current) ? "" : current ?? "";
        return to.Draft ?? "";
    }
}

/// <summary>
/// CHAT13: follows the game's channel each frame to learn the player's picks, notices the input opening, and arms the
/// whisper prefill once per opening. Game-free.
/// </summary>
internal sealed class ChatTabInputTracker
{
    private int _lastMode = -1;
    private bool _wasFocused;
    private bool _prefilled;

    /// <summary>The channel Progenitor just set (a tab switch, an opening): not the player's choice.</summary>
    internal void Applied(int mode)
    {
        if (mode >= 0) _lastMode = mode;
    }

    /// <summary>A tab switch: the next empty focused line may get the new tab's prefill.</summary>
    internal void TabChanged() => _prefilled = false;

    internal void Reset()
    {
        _lastMode = -1;
        _wasFocused = false;
        _prefilled = false;
    }

    /// <summary>
    /// One frame. A channel change seen while the player types is offered to <paramref name="tab"/> (returns true when
    /// the tab's remembered channel changed: save the tabs); a change while the input is closed (a world load) is only
    /// noted. <paramref name="opened"/> is true on the frame the input gains focus.
    /// </summary>
    internal bool Observe(ChatTab? tab, int mode, bool focused, out bool opened)
    {
        opened = focused && !_wasFocused;
        bool changed = false;
        if (focused && !opened && _lastMode >= 0 && mode != _lastMode && tab != null) changed = ChatTabInput.Remember(tab, mode);
        _lastMode = mode;
        if (!focused) _prefilled = false;
        _wasFocused = focused;
        return changed;
    }

    /// <summary>
    /// The line to put in the input this frame, or null: the tab's whisper prefill, once per opening (or tab switch),
    /// while the input is open and empty and no insert is waiting to land.
    /// </summary>
    internal string? Prefill(ChatTab? tab, string? partner, bool focused, string? text, bool insertPending)
    {
        if (!focused || _prefilled || tab == null) return null;
        if (insertPending) return null;
        _prefilled = true;
        // Something is already typed: this opening never gets a prefill.
        if (!string.IsNullOrEmpty(text)) return null;
        return ChatTabInput.PrefillFor(tab, partner);
    }
}

/// <summary>
/// CHAT7 and CHAT13: when a typed line counts as sent, for the sent-line recall. A line counts when the input loses
/// focus with the line emptied (the game empties it when Enter sends); not when the focus went to a press on the tab
/// row (a tab click: the line becomes that tab's draft), not for a bare whisper line, and not after the line was
/// swapped for another tab's (<see cref="Forget"/>). Escape on a typed line may count; recall then offers it, which is
/// harmless. Game-free.
/// <para>
/// When Enter sends, the game empties the input at once but reports the focus loss
/// (<c>HUDChatWindow.IsChatFocused</c>) only from its next chat update, so the line is seen empty while still focused
/// for a frame first. A line emptied all at once is therefore kept for up to <see cref="ClearedFrames"/> focused
/// frames and counts if the focus drops in that time.
/// </para>
/// </summary>
internal sealed class ChatSentWatch
{
    /// <summary>How many focused frames a line emptied all at once still counts as the typed line.</summary>
    internal const int ClearedFrames = 2;

    private string _typed = "";
    private string _cleared = "";
    private int _clearedFrames;
    private bool _wasFocused;

    /// <summary>
    /// The line typed so far while the input has focus, or the line the game just emptied when it sent it (empty after
    /// <see cref="Forget"/>).
    /// </summary>
    internal string Typed => _typed.Length > 0 ? _typed : _cleared;

    /// <summary>The last focus loss went to a press on the tab row.</summary>
    internal bool LastLossWasTabClick { get; private set; }

    /// <summary>
    /// One frame: the line that was just sent, or null. <paramref name="lostToTabRow"/> is asked, with the typed line,
    /// only on the frame focus is lost.
    /// </summary>
    internal string? Frame(bool focused, string? text, Func<string, bool> lostToTabRow)
    {
        string? sent = null;
        if (focused)
        {
            string now = text ?? "";
            if (now.Length == 0 && _typed.Length > 0)
            {
                // Emptied all at once: the game's send, whose focus loss shows a frame later.
                _cleared = _typed;
                _clearedFrames = 1;
            }
            else if (now.Length > 0 || ++_clearedFrames > ClearedFrames) _cleared = "";
            _typed = now;
        }
        else if (_wasFocused)
        {
            string typed = Typed;
            _typed = "";
            _cleared = "";
            bool tabClick = typed.Length > 0 && lostToTabRow(typed);
            LastLossWasTabClick = tabClick;
            if (!tabClick && string.IsNullOrEmpty(text) && typed.Trim().Length > 0 && !ChatTabInput.IsBareWhisper(typed)) sent = typed;
        }

        _wasFocused = focused;
        return sent;
    }

    /// <summary>Whether the input loses focus this frame (the caller ends recall browsing then).</summary>
    internal bool LostFocus(bool focused) => _wasFocused && !focused;

    /// <summary>The line in the input was replaced (another tab's draft, a long message sent by parts): forget it.</summary>
    internal void Forget()
    {
        _typed = "";
        _cleared = "";
    }
}

/// <summary>What a pending chat insert does on this frame (<see cref="ChatInsertPlan.Next"/>).</summary>
internal enum ChatInsertStep
{
    /// <summary>Keep waiting.</summary>
    Wait,

    /// <summary>Open the chat input now (the menu it waited for has closed).</summary>
    OpenChat,

    /// <summary>The chat has focus: put the text in the line.</summary>
    Apply,

    /// <summary>The chat never opened: forget the text.</summary>
    Drop,
}

/// <summary>
/// CHAT4 and the map share: when a pending insert lands, game-free. An insert opens the chat at once and waits up to
/// <see cref="FocusFrames"/> frames for it to take focus. An insert made while the game's full-screen menu closes (the
/// map share closes the map first: the chat input is not usable over the menu) waits for the menu to be gone, up to
/// <see cref="MenuFrames"/> frames, then opens the chat and waits as above.
/// </summary>
internal static class ChatInsertPlan
{
    /// <summary>How long an insert waits for the chat to take focus (about 5 s at 60 fps).</summary>
    internal const int FocusFrames = 300;

    /// <summary>How long an insert waits for the game's menu to close before it gives up (about 5 s at 60 fps).</summary>
    internal const int MenuFrames = 300;

    /// <param name="focused">The chat input has focus.</param>
    /// <param name="waitForMenu">The insert still waits for the menu to close before it opens the chat.</param>
    /// <param name="menuOpen">The game's full-screen menu is open.</param>
    /// <param name="frames">Frames this phase has waited, counting this one.</param>
    internal static ChatInsertStep Next(bool focused, bool waitForMenu, bool menuOpen, int frames)
    {
        if (waitForMenu)
        {
            if (!menuOpen) return focused ? ChatInsertStep.Apply : ChatInsertStep.OpenChat;
            return frames > MenuFrames ? ChatInsertStep.Drop : ChatInsertStep.Wait;
        }

        if (focused) return ChatInsertStep.Apply;
        return frames > FocusFrames ? ChatInsertStep.Drop : ChatInsertStep.Wait;
    }
}
