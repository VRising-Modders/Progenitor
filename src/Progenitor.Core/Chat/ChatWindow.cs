#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Progenitor.Hooks;
using Progenitor.Inputs;
using Progenitor.Ui;
using ProjectM;
using ProjectM.Network;
using ProjectM.UI;
using Progenitor.Ecs;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Chat;

/// <summary>
/// CHAT1: Progenitor's improvements to the game's own chat box, stage 1. Built into Core, client only, no
/// plugin API. Settings are in <c>[Chat]</c> of <c>progenitor.ui.cfg</c> and on Progenitor's page (Your
/// settings, Chat).
///
/// <list type="bullet">
/// <item><b>Longer chat visibility.</b> The game fades the unfocused chat box 14 seconds after the last
/// message (<see cref="ChatVisibility"/>). Progenitor keeps it up for <c>VisibleSeconds</c> (default 30),
/// while the pointer rests on it, or always.</item>
/// <item><b>Click-safe chat box.</b> A click on the chat box no longer attacks or turns the camera
/// (<see cref="ChatClickGuard"/>, the order-97 context in <see cref="GameInputBlocker"/>).</item>
/// <item><b>CHAT2: <c>/r</c> and name completion.</b> <c>/r </c> becomes the game's <c>/reply </c> as it is typed,
/// and the completion key (Tab, a <see cref="KeybindScope.Chat"/> bind) completes a player's name after
/// <c>/w </c> or <c>/whisper </c> (<see cref="ChatCommandLogic"/>). The game cycles the chat channel on Tab by
/// reading the keyboard itself in its chat update (<c>Keyboard.current[Key.Tab].wasPressedThisFrame</c>, not an
/// input action), which no input context can hold back; so while a completion would act on a Tab press, the
/// channel the update changed is put back right after it (<see cref="AfterChatUpdate"/>).</item>
/// </list>
///
/// <para><b>Where it hangs.</b> <see cref="SystemHooks.Before{TSystem}(Action{SystemHookContext}, HookOptions?)"/>
/// on <c>ProjectM.UI.ClientChatSystem</c>, so the visibility time is written just before the game's update
/// reads it. Nothing native is patched for this module; the click guard shares the input mask detour the
/// mod panels already use. Fields read: <c>_ChatWindow</c>, <c>_ChatMessages</c>, <c>_CurrentState</c>,
/// <c>_MessageTime</c> (research: chat-client-feasibility-2026-10-07).</para>
/// </summary>
internal static class ChatWindow
{
    internal const string Section = "Chat";
    internal const string VisibleSecondsKey = "VisibleSeconds";
    internal const string KeepVisibleKey = "KeepVisible";
    internal const string StayWhileHoveredKey = "StayWhileHovered";
    internal const string ClickSafeKey = "ClickSafe";
    internal const string ReplyShortcutKey = "ReplyShortcut";
    internal const string NameCompletionKey = "NameCompletion";

    /// <summary>CHAT2: the completion key's id in <c>[Keybinds]</c> and the Keybinds tab.</summary>
    internal const string CompleteNameKeybindId = "ChatCompleteName";

    internal static readonly KeyChord DefaultCompleteNameKey = new(UiKey.Tab);

    /// <summary><c>ChatWindowState.OpenUnfocused</c>: the only state the game fades in.</summary>
    private const int StateOpenUnfocused = 2;

    /// <summary><c>ChatWindowState.Close</c>.</summary>
    private const int StateClose = 3;

    /// <summary>Retry a refused click-guard registration at most this often.</summary>
    private const int RegisterRetryFrames = 60;

    /// <summary>Ask the game whether a registered click guard is still in its input list this often.</summary>
    private const int VerifyFrames = 600;

    /// <summary>Re-read the game's mouse bindings this often (the cached snapshot makes it cheap).</summary>
    private const int BindingsRefreshFrames = 600;

    private static ConfigEntry<int>? _visibleSeconds;
    private static ConfigEntry<bool>? _keepVisible;
    private static ConfigEntry<bool>? _stayWhileHovered;
    private static ConfigEntry<bool>? _clickSafe;
    private static ConfigEntry<bool>? _replyShortcut;
    private static ConfigEntry<bool>? _nameCompletion;
    private static IKeybindHandle? _completeKey;
    private static IKeybindHandle? _recallUpKey;
    private static IKeybindHandle? _recallDownKey;
    private static ConfigEntry<bool>? _recall;

    /// <summary>CHAT7: sent-line recall (Up/Down while typing), kept in <c>progenitor-cache/chat/sent.txt</c>.</summary>
    private static readonly ChatRecall Recall = new();
    private static bool _recallLoaded;
    /// <summary>CHAT7/CHAT13: when a typed line counts as sent (<see cref="ChatSentWatch"/>).</summary>
    private static readonly ChatSentWatch SentWatch = new();

    internal const string RecallUpKeybindId = "ChatRecallPrevious";

    /// <summary>CHAT9: the long-message send key (Enter, a chat-scope bind beside the game's own Enter).</summary>
    internal const string SendLongKeybindId = "ChatSendLong";
    private static IKeybindHandle? _sendLongKey;
    private static ConfigEntry<bool>? _longMessages;

    /// <summary>Parts waiting to go out, one every few frames so they arrive in order.</summary>
    private static readonly Queue<(string Text, ChatMessageType Type, NetworkId Whisper)> LongParts = new();
    private static int _longPartFrame;
    private static int _gameCharacterLimit = -1;
    internal const string RecallDownKeybindId = "ChatRecallNext";
    private static readonly ChatNameCompleter Completer = new();

    /// <summary>CHAT3: the latest whisper partner, for <c>/r</c>.</summary>
    private static readonly ChatWhisperMemory Whispers = new();

    /// <summary>
    /// CHAT13: the input line was swapped for another tab's: what was typed is that tab's draft, not a sent line, and
    /// Up recall starts again from the newest line.
    /// </summary>
    internal static void ForgetTypedLine()
    {
        SentWatch.Forget();
        Recall.EndBrowsing();
    }

    /// <summary>CHAT13: the latest whisper partner (<c>/r</c>'s), for a whisper tab's <c>/w partner </c> line; or empty.</summary>
    internal static string WhisperPartner => Whispers.Partner;

    /// <summary>CHAT4: text waiting to be put in the chat input once the chat has focus (<see cref="InsertIntoInput"/>).</summary>
    private static string? _pendingInsert;
    private static int _pendingInsertFrames;

    /// <summary>How long a pending insert waits for the chat to open before it is dropped (about 5 s at 60 fps).</summary>
    private const int PendingInsertMaxFrames = 300;

    /// <summary>
    /// CHAT4: put <paramref name="text"/> at the end of the chat input for the player to finish and send (the Mods
    /// menu's Share in chat). Opens the chat when it is closed. Never sends. False when there is no client world or
    /// chat window (the title screen).
    /// </summary>
    internal static bool InsertIntoInput(string text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return false;
            var system = world.GetExistingSystemManaged<ClientChatSystem>();
            var window = system?._ChatWindow;
            if (window == null || window.Pointer == IntPtr.Zero) return false;
            _pendingInsert = text;
            _pendingInsertFrames = 0;
            if (!window.IsChatFocused) ChatHelper.ToggleChat(world);
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: insert into the chat input threw: " + e.Message);
            return false;
        }
    }

    /// <summary>Apply a pending insert once the chat has focus; drop it when the chat never opens.</summary>
    private static void ApplyPendingInsert(HUDChatWindow window)
    {
        if (_pendingInsert == null) return;
        if (!window.IsChatFocused)
        {
            if (++_pendingInsertFrames > PendingInsertMaxFrames) _pendingInsert = null;
            return;
        }

        var field = window.ChatInputField;
        if (field == null) return;
        string current = field.text ?? "";
        // CHAT13: a whisper insert replaces a bare whisper line (a whisper tab's prefill) instead of following it.
        string joined = ChatTabInput.JoinInsert(current, _pendingInsert);
        _pendingInsert = null;
        SetLine(field, joined);
    }

    private static IDisposable? _hook;
    private static IDisposable? _afterHook;
    private static bool _installed;

    /// <summary>CHAT2: the channel before the game's chat update, kept when a Tab press would complete a name.</summary>
    private static int _modeBefore = -1;
    private static bool _frameHooked;

    /// <summary>The <c>Time.frameCount</c> of the last chat-system update; the watchdog drops the guard when it stops.</summary>
    private static int _lastHookFrame;

    private static World? _world;
    private static int _lastCount = -1;
    private static IntPtr _lastEntry;
    /// <summary>The entry pointers of the last read of the game's chat list, oldest first (<see cref="ChatNewEntries"/>).</summary>
    private static List<IntPtr> _seen = new();
    private static float _lastActivity = float.NegativeInfinity;
    private static float _lastWheel;
    private static bool _lastWheelHad;
    private static int _frame;
    private static int _retryFrame;
    private static int _bindingsFrame = int.MinValue;
    private static bool _warned;

    internal static int VisibleSeconds => ChatVisibility.ClampSeconds(_visibleSeconds?.Value ?? ChatVisibility.DefaultVisibleSeconds);
    internal static bool KeepVisible => _keepVisible?.Value ?? false;
    internal static bool StayWhileHovered => _stayWhileHovered?.Value ?? true;
    internal static bool ClickSafe => _clickSafe?.Value ?? true;
    internal static bool ReplyShortcut => _replyShortcut?.Value ?? true;
    internal static bool NameCompletion => _nameCompletion?.Value ?? true;

    internal static void Install()
    {
        if (Plugin.IsServerProcess || _installed) return;
        var file = UiKeybindHost.CoreConfig;
        if (file != null)
        {
            try
            {
                _visibleSeconds = file.Bind(Section, VisibleSecondsKey, ChatVisibility.DefaultVisibleSeconds, new ConfigDescription(
                    "Seconds the chat box stays on screen after the last message before it fades. The game's own is 14.",
                    new AcceptableValueRange<int>(ChatVisibility.MinVisibleSeconds, ChatVisibility.MaxVisibleSeconds)));
                _keepVisible = file.Bind(Section, KeepVisibleKey, false,
                    "Never fade the chat box; Escape still closes it.");
                _stayWhileHovered = file.Bind(Section, StayWhileHoveredKey, true,
                    "Keep the chat box up while the mouse pointer is over it.");
                _clickSafe = file.Bind(Section, ClickSafeKey, true,
                    "Clicking the chat box does not attack, turn the camera or use another mouse action.");
                _replyShortcut = file.Bind(Section, ReplyShortcutKey, true,
                    "Typing /r at the start of a chat line becomes /reply, which answers the last whisper.");
                _nameCompletion = file.Bind(Section, NameCompletionKey, true,
                    "After /w or /whisper, the completion key (Tab by default, under Keybinds) fills in an online "
                    + "player's name; press it again for the next match.");
                BindViewSettings(file);
                _recall = file.Bind(Section, "Recall", true,
                    "While typing in chat, Up brings back the lines you sent (commands too) and Down goes forward again.");
                _commandHints = file.Bind(Section, "CommandHints", true,
                    "While a chat line starts with \".\", show the server's matching commands above it; Tab completes the first.");
                _testNames = file.Bind(Section, "TestNames", "",
                    "Testing only: extra names the /w suggestions offer as if they were online, separated by commas. They are not players; a whisper to one gets the game's not-found answer. Leave empty.");
                _modSuggest = file.Bind(Section, "ModSuggestions", true,
                    "Type [mod: and part of a mod's name: matching mods show above the line (installed mods, and Thunderstore once the Mods menu has loaded it). Tab puts the first in as a mod link; press again for the next.");
                _channelColor = file.Bind(Section, "ChannelColor", true,
                    "The line you are typing takes its channel's color (Global, Clan, Whisper, Local), so you can see where it goes.");
                _longMessages = file.Bind(Section, "LongMessages", true,
                    "A chat line longer than the game takes is sent in parts, joined back into one line for players with Progenitor.");
                ChatView.MutedPlayers = file.Bind(Section, "MutedPlayers", "",
                    "Players whose chat lines Progenitor's chat view leaves out, separated by commas (right-click a line to mute).");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Chat: settings could not be bound: " + e.Message);
            }
        }

        try
        {
            _hook = SystemHooks.Before<ClientChatSystem>(BeforeChatUpdate);
            _afterHook = SystemHooks.After<ClientChatSystem>(AfterChatUpdate);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: ClientChatSystem hook install threw: " + e.Message);
            try { _hook?.Dispose(); } catch { /* */ }
            _hook = null;
            return;
        }

        if (file != null)
        {
            try
            {
                _completeKey = Keybinds.RegisterScoped(typeof(ChatWindow), SettingsPanel.PluginId, CompleteNameKeybindId, file,
                    DefaultCompleteNameKey,
                    "Complete a player's name after /w in chat (press again for the next match).",
                    CompleteName, null, null, allowWhileCaptured: false, allowWhenChatUnknown: false, KeybindScope.Chat);
                _recallUpKey = Keybinds.RegisterScoped(typeof(ChatWindow), SettingsPanel.PluginId, RecallUpKeybindId, file,
                    new KeyChord(UiKey.UpArrow), "Bring back the previous line you sent in chat.",
                    () => RecallStep(up: true), null, null, allowWhileCaptured: false, allowWhenChatUnknown: false, KeybindScope.Chat);
                _sendLongKey = Keybinds.RegisterScoped(typeof(ChatWindow), SettingsPanel.PluginId, SendLongKeybindId, file,
                    new KeyChord(UiKey.Enter), "Send a long chat line in parts, and turn /loc into your location (the game's own Enter sends ordinary lines).",
                    OnSendKey, null, null, allowWhileCaptured: false, allowWhenChatUnknown: false, KeybindScope.Chat);
                _recallDownKey = Keybinds.RegisterScoped(typeof(ChatWindow), SettingsPanel.PluginId, RecallDownKeybindId, file,
                    new KeyChord(UiKey.DownArrow), "Go forward through the lines you sent in chat.",
                    () => RecallStep(up: false), null, null, allowWhileCaptured: false, allowWhenChatUnknown: false, KeybindScope.Chat);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("Chat: the name completion key could not be registered: " + e.Message);
            }
        }

        // CHAT4: mod links in chat lines and their clicks (the Mods menu's resolver).
        ChatFormat.ModTokenFinder = ModLinkToken.Find;
        ChatView.OpenModLink = ModLinks.Open;
        // CHAT8: item links show the item's name.
        ChatFormat.ItemNameResolver = ChatGameData.ItemName;
        // Servant mission tokens show the mission's name, as the game's hunt notices print it.
        ChatFormat.MissionNameResolver = ChatGameData.MissionName;
        ChatInputTokens.Position = () => ChatGameData.TryLocalPosition(out float x, out float z) ? (true, x, z) : (false, 0f, 0f);
        ChatInputTokens.ItemIdByPrefabName = ChatGameData.ItemIdByPrefabName;

        Plugin.Frame += Watchdog;
        _frameHooked = true;
        _installed = true;
        Plugin.Logger?.LogInfo("Chat: installed (visible=" + VisibleSeconds + "s keep=" + KeepVisible
            + " hover=" + StayWhileHovered + " clickSafe=" + ClickSafe + " reply=" + ReplyShortcut
            + " completion=" + NameCompletion + ").");
    }

    /// <summary>CHAT5/CHAT6: the chat view's and the tab strip's settings, in <c>[Chat]</c>.</summary>
    private static void BindViewSettings(ConfigFile file)
    {
        ChatView.ViewEnabled = file.Bind(Section, "View", true,
            "Progenitor draws the chat box's lines: tabs, longer history, links, mentions and repeat counts. Off: the game's own lines.");
        ChatView.ReplacedBy = file.Bind(Section, "ReplacedBy", "",
            "Plugin GUIDs or names, separated by commas, of mods that replace the game's chat window. When one is loaded, Progenitor's chat view stays off.");
        ChatView.Timestamps = file.Bind(Section, "Timestamps", false,
            "Show the time before each line (skipped when the game's own timestamp option is on).");
        ChatView.CollapseRepeats = file.Bind(Section, "CollapseRepeats", true,
            "The same line again within a minute raises a count (×3) instead of adding a line.");
        ChatView.KeepHistory = file.Bind(Section, "KeepHistory", true,
            "Keep each server's chat across sessions, with a line where each session started and ended.");
        ChatView.HistoryLines = file.Bind(Section, "HistoryLines", ChatLog.DefaultCapacity, new ConfigDescription(
            "How many lines of each server's chat are kept (50 to 5000). Changes apply at once: a lower number drops the oldest lines.",
            new AcceptableValueRange<int>(ChatLog.MinCapacity, ChatLog.MaxCapacity)));
        // The default went from 500 to 5000 (2026-10-17): an untouched old default moves up once (ChatHistorySetting).
        var raised = file.Bind(Section, ChatHistorySetting.RaisedKey, false,
            "Set once [Chat] HistoryLines was checked for the old 500-line default after the default became 5000. Leave it.");
        if (!raised.Value)
        {
            int kept = ChatHistorySetting.Upgrade(ChatView.HistoryLines.Value, alreadyRaised: false);
            if (kept != ChatView.HistoryLines.Value)
            {
                ChatView.HistoryLines.Value = kept;
                Plugin.Logger?.LogInfo("Chat: [Chat] HistoryLines was the old default 500; it is now " + kept + ".");
            }

            raised.Value = true;
        }
        ChatView.Mentions = file.Bind(Section, "Mentions", true,
            "Highlight a line that says your name, one of your watched words, or @all from an admin (when the server allows it).");
        ChatView.MentionSound = file.Bind(Section, "MentionSound", true, "Play the whisper sound for a mention.");
        ChatView.MentionWords = file.Bind(Section, "MentionWords", "",
            "Extra words that count as a mention, separated by commas (at most 20).");
        ChatView.Colors = file.Bind(Section, "Colors", "",
            "Your chat colors by channel: Global=#7AB8F5;Clan=#8ED86E (set from the chat box's menu, Colors). A channel not listed keeps the game's color.");
        ChatView.FontSize = file.Bind(Section, "FontSize", 0, new ConfigDescription(
            "The chat lines' text size; 0 keeps the game's.", new AcceptableValueRange<int>(0, 40)));
        ChatTabs.TabsSetting = file.Bind(Section, "Tabs", ChatTab.DefaultSaved,
            "Your chat tabs after All: Name=channels;Name=channels (set from the chat box's tab menu).");
        ChatTabs.ActiveSetting = file.Bind(Section, "ActiveTab", 0, "The tab shown (0 is All).");
        ChatTabs.OffsetX = file.Bind(Section, "OffsetX", 0f, "How far the chat box is moved from the game's place, across.");
        ChatTabs.OffsetY = file.Bind(Section, "OffsetY", 0f, "How far the chat box is moved from the game's place, up.");
        ChatTabs.Width = file.Bind(Section, "Width", 0f, "The chat box's width; 0 keeps the game's.");
        ChatTabs.Height = file.Bind(Section, "Height", 0f, "The chat box's height; 0 keeps the game's.");
        ChatTabs.Opacity = file.Bind(Section, "BackgroundOpacity", -1, new ConfigDescription(
            "The chat box background's opacity in percent; -1 keeps the game's.", new AcceptableValueRange<int>(-1, 100)));
        ChatTabs.Locked = file.Bind(Section, "LockPosition", false, "Stop the chat box from being moved or resized by dragging.");
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        ChatFormat.ModTokenFinder = null;
        ChatView.OpenModLink = null;
        ChatFormat.ItemNameResolver = null;
        ChatFormat.MissionNameResolver = null;
        ChatInputTokens.Position = null;
        ChatInputTokens.ItemIdByPrefabName = null;
        try { ChatView.Uninstall(); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: chat view uninstall threw: " + e.Message); }
        ChatTabArt.Release();
        try { _hook?.Dispose(); } catch { /* */ }
        _hook = null;
        try { _afterHook?.Dispose(); } catch { /* */ }
        _afterHook = null;
        _modeBefore = -1;
        if (_frameHooked)
        {
            try { Plugin.Frame -= Watchdog; } catch { /* */ }
            _frameHooked = false;
        }

        try { _completeKey?.Dispose(); } catch { /* */ }
        _completeKey = null;
        try { _recallUpKey?.Dispose(); } catch { /* */ }
        _recallUpKey = null;
        try { _recallDownKey?.Dispose(); } catch { /* */ }
        _recallDownKey = null;
        try { _sendLongKey?.Dispose(); } catch { /* */ }
        _sendLongKey = null;
        LongParts.Clear();
        RestoreInput();
        DestroyHints();
        ChatClickGuard.Reset();
        try { GameInputBlocker.RemoveChatGuard(force: true); }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: click guard removal threw: " + e.Message); }
        ForgetWorld();
    }

    private static void ForgetWorld()
    {
        _world = null;
        _retryFrame = 0;
        _canvasOwner = IntPtr.Zero;
        _canvas = null;
        _lastCount = -1;
        _lastEntry = IntPtr.Zero;
        _seen = new List<IntPtr>();
        _lastActivity = float.NegativeInfinity;
        _lastWheel = 0f;
        _lastWheelHad = false;
        _bindingsFrame = int.MinValue;
        Completer.Reset();
        Whispers.Reset();
        ChatTabs.ForgetInput();
        _pendingInsert = null;
        LongParts.Clear();
        RestoreInput();
        DestroyHints();
        _commandIndex = -1;
    }

    /// <summary>
    /// The input field's own character limit and text color, put back on unload and world change (a later generation
    /// must never read Progenitor's values as the game's).
    /// </summary>
    private static TMPro.TMP_InputField? _limitedField;

    private static void RestoreInput()
    {
        try
        {
            var field = _limitedField;
            if (field != null && field.Pointer != IntPtr.Zero)
            {
                if (_gameCharacterLimit >= 0) field.characterLimit = _gameCharacterLimit;
                if (_gameInputColorKnown && field.textComponent != null) field.textComponent.color = _gameInputColor;
            }
        }
        catch { /* the field went with its window */ }

        _limitedField = null;
        _gameCharacterLimit = -1;
        _coloredMode = -1;
        _gameInputColorKnown = false;
    }

    /// <summary>
    /// The guard is decided in the chat system's update; if that update stops (world unloading, the system
    /// disabled), nothing would ever clear an armed guard, so a frame without one drops it.
    /// </summary>
    private static void Watchdog()
    {
        if (!ChatClickGuard.AnyActive) return;
        int frame;
        try { frame = Time.frameCount; }
        catch { return; }
        if (frame - _lastHookFrame > 2) ChatClickGuard.Reset();
    }

    private static void BeforeChatUpdate(SystemHookContext ctx)
    {
        _frame++;
        try { _lastHookFrame = Time.frameCount; } catch { /* */ }
        try
        {
            var world = ctx?.World;
            if (world == null || !world.IsCreated) return;
            if (!ReferenceEquals(world, _world))
            {
                ForgetWorld();
                ChatView.OnWorldChanged();
                ChatClickGuard.Reset();
                // The old world's list went with it; this drops the object (and removes it if it is somehow still listed).
                GameInputBlocker.RemoveChatGuard(force: true);
                _world = world;
            }

            var system = world.GetExistingSystemManaged<ClientChatSystem>();
            if (system == null || system.Pointer == IntPtr.Zero)
            {
                ChatClickGuard.Update(false, false, false);
                ChatClickGuard.UpdateWheel(false);
                return;
            }

            var window = system._ChatWindow;
            if (window == null || window.Pointer == IntPtr.Zero)
            {
                ChatClickGuard.Update(false, false, false);
                ChatClickGuard.UpdateWheel(false);
                return;
            }

            float now = Time.time;
            NoteNewMessages(system, window, world, now);

            int state = (int)system._CurrentState;
            bool visible = state != StateClose && IsShown(window);
            bool overChat = visible && PointerOver(window);
            if (overChat && StayWhileHovered) _lastActivity = now;
            UpdateWheel(window, state, visible, overChat, now);
            // The colors panel keeps the chat shown while it is open.
            if (ChatColorPanel.IsOpen) _lastActivity = now;

            if (state == StateOpenUnfocused)
            {
                float? write = ChatVisibility.MessageTimeToWrite(now, system._MessageTime, _lastActivity, VisibleSeconds, KeepVisible);
                if (write.HasValue) system._MessageTime = write.Value;
            }

            NoteSent(window);
            AllowLongInput(window);
            SendLongParts(world);
            UpdateCommands(system, window);
            ChatView.Update(window, visible, overChat, system._HUDChatParent);
            UpdateClickGuard(world, overChat);
        }
        catch (Exception e)
        {
            ChatClickGuard.Reset();
            if (_warned) return;
            _warned = true;
            Plugin.Logger?.LogWarning("Chat: update threw (logged once): " + e);
        }
    }

    /// <summary>
    /// A new message is a change in the count of <c>_ChatMessages</c> or in its last entry. The game caps the list at
    /// 50 and drops from the front, so a full list keeps its count while lines arrive: which entries are new comes from
    /// lining up this read with the previous one (<see cref="ChatNewEntries"/>), so every line of a burst is taken,
    /// not only the newest. The first read in a world only records the baseline.
    /// </summary>
    private static void NoteNewMessages(ClientChatSystem system, HUDChatWindow window, World world, float now)
    {
        var list = system._ChatMessages;
        int count = list == null ? 0 : list.Count;
        IntPtr last = count > 0 && list![count - 1] != null ? list[count - 1].Pointer : IntPtr.Zero;
        bool first = _lastCount < 0;
        if (!first && count == _lastCount && last == _lastEntry) return;

        var entries = new ChatEntry?[count];
        var pointers = new List<IntPtr>(count);
        for (int i = 0; i < count; i++)
        {
            var entry = list![i];
            entries[i] = entry;
            pointers.Add(entry == null ? IntPtr.Zero : entry.Pointer);
        }

        if (first)
        {
            // CHAT5: the lines the game already had when this world's chat was first read go into the view's log.
            for (int i = 0; i < count; i++)
                if (entries[i] != null) ChatView.Ingest(entries[i]!, world, null);
        }
        else if (count > 0)
        {
            _lastActivity = now;
            // CHAT3: whispers among the new entries set the /r partner. CHAT5: the same entries go into the chat view's log.
            for (int i = ChatNewEntries.FirstNew(_seen, pointers); i < count; i++)
            {
                if (entries[i] == null) continue;
                NoteWhisper(entries[i]);
                ChatView.Ingest(entries[i]!, world, window);
            }
        }

        _seen = pointers;
        _lastCount = count;
        _lastEntry = last;
    }

    private static void NoteWhisper(ChatEntry? entry)
    {
        if (entry == null) return;
        var type = entry.MessageType;
        if (type == ServerChatMessageType.WhisperFrom) Whispers.NoteReceived(entry.User);
        else if (type == ServerChatMessageType.WhisperTo) Whispers.NoteSent();
    }

    private static IntPtr _canvasOwner;
    private static Canvas? _canvas;

    /// <summary>
    /// Drawn on screen: active, not faded out, and its canvas enabled. Photo mode and other HUD hides switch
    /// the HUD canvas off without touching the chat's own objects, and an invisible box must not take clicks.
    /// </summary>
    private static bool IsShown(HUDChatWindow window)
    {
        var go = window.gameObject;
        if (go == null || !go.activeInHierarchy) return false;
        var group = window.ContentCanvasGroup;
        if (group != null && group.alpha <= 0.01f) return false;
        if (_canvasOwner != window.Pointer || _canvas == null)
        {
            _canvasOwner = window.Pointer;
            _canvas = window.GetComponentInParent<Canvas>();
        }

        var canvas = _canvas;
        if (canvas == null) return true;
        if (!canvas.isActiveAndEnabled) return false;
        var root = canvas.rootCanvas;
        return root == null || root.isActiveAndEnabled;
    }

    /// <summary>
    /// The wheel over the chat box (<see cref="ChatWheelScroll"/>): a notch over the shown box restarts the fade
    /// countdown, and while the box lingers after the input closes, when the game's UI does not scroll the log,
    /// Progenitor moves the log itself. The camera zoom is taken separately (<see cref="ChatClickGuard.WheelBlocked"/>).
    /// </summary>
    private static void UpdateWheel(HUDChatWindow window, int state, bool visible, bool overChat, float now)
    {
        float wheel = UnityUiRuntime.SampleWheelDeltaY();
        bool notch = UiShownInput.IsNewNotch(wheel, _lastWheel, _lastWheelHad);
        _lastWheel = wheel;
        _lastWheelHad = UiShownInput.HasWheelDelta(wheel);
        if (ChatWheelScroll.KeepsVisible(visible, overChat, notch)) _lastActivity = now;
        if (!ChatWheelScroll.ProgenitorScrolls(state, visible, overChat, notch, UnityInputRuntime.AnyMouseButtonHeld())) return;

        var scroll = window.ScrollView;
        if (scroll == null || scroll.Pointer == IntPtr.Zero) return;
        var content = scroll.content;
        var viewport = scroll.viewport;
        if (content == null || viewport == null) return;
        float position = scroll.verticalNormalizedPosition;
        float next = ChatWheelScroll.NextPosition(position, wheel, content.rect.height, viewport.rect.height);
        if (next - position > 0.0001f || position - next > 0.0001f)
        {
            scroll.velocity = Vector2.zero;
            scroll.verticalNormalizedPosition = next;
        }
    }

    /// <summary>
    /// The pointer is over the message log (the same rect the game's own hover test uses, with the same null
    /// camera), or over the input line while it is shown, in screen pixels.
    /// </summary>
    private static bool PointerOver(HUDChatWindow window)
    {
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y)) return false;
        var point = new Vector2(x, y);
        var log = window.ScrollViewGraphic;
        if (log != null && RectTransformUtility.RectangleContainsScreenPoint(log.rectTransform, point, null)) return true;
        // The log's scroll bar sits just outside the log's rect (live 2026-10-07: x 22-34 beside a log at x 36+);
        // a click on it must not attack either.
        var scrollbar = window.ScrollView != null ? window.ScrollView.verticalScrollbar : null;
        if (scrollbar != null && scrollbar.gameObject.activeInHierarchy)
        {
            var barRect = scrollbar.transform.TryCast<RectTransform>();
            if (barRect != null && RectTransformUtility.RectangleContainsScreenPoint(barRect, point, null)) return true;
        }

        // CHAT6: the tab strip and the resize grip.
        if (ChatTabs.PointerOver(point)) return true;

        var input = window.ChatInputField;
        if (input == null || !input.gameObject.activeInHierarchy) return false;
        var inputRect = input.transform.TryCast<RectTransform>();
        return inputRect != null && RectTransformUtility.RectangleContainsScreenPoint(inputRect, point, null);
    }

    /// <summary>
    /// CHAT2, each frame while the player types in chat: <c>/r </c> becomes <c>/reply </c>, and when a Tab press would
    /// complete a name, the channel is kept so <see cref="AfterChatUpdate"/> can undo the game's Tab cycle.
    /// </summary>
    private static void UpdateCommands(ClientChatSystem system, HUDChatWindow window)
    {
        _modeBefore = -1;
        // A pending insert lands first, so a whisper tab's prefill never goes in front of it.
        ApplyPendingInsert(window);
        // CHAT13: the shown tab's channel, the player's channel choices, and the whisper tab's line.
        ChatTabs.UpdateInput(system, window, _pendingInsert != null);
        ColorInput(system, window);
        bool reply = ReplyShortcut, completion = NameCompletion;
        if (!window.IsChatFocused)
        {
            // A closed or sent line ends a completion: retyping the same text later starts over.
            Completer.Reset();
            _commandIndex = -1;
            _modIndex = -1;
            _hintFor = "\u0001";
            if (_hintRoot != null && _hintRoot.Pointer != IntPtr.Zero && _hintRoot.activeSelf) _hintRoot.SetActive(false);
            return;
        }

        var field = window.ChatInputField;
        if (field == null) return;

        string text = field.text ?? "";
        // CHAT3: the whisper about to be sent names its target (typed /w name, or the game's whisper mode).
        Whispers.NoteDraft(text);
        if (system._DefaultMode == ChatMessageType.Whisper) Whispers.NoteWhisperMode(system._WhisperUserName);

        string replyLine = Whispers.ReplyLine();
        if (reply && ChatCommandLogic.TryExpandReply(text, replyLine, out string expanded))
        {
            // Keep the caret where the player was typing (the line grew before it).
            int caret = field.stringPosition;
            field.text = expanded;
            field.stringPosition = Math.Min(expanded.Length, caret + (replyLine.Length - ChatCommandLogic.ReplyShort.Length));
            text = expanded;
        }

        // CHAT8: /loc and /item at the start of the line become link tokens.
        if (ChatInputTokens.TryExpand(text, out string tokens))
        {
            SetLine(field, tokens);
            text = tokens;
        }

        UpdateHints(window, text);
        if (!completion) return;

        if (CompletionKeyIsTab() && ((NameCompletion && Completer.WouldAct(text)) || CommandWouldComplete(text) || ModWouldComplete(text)))
            _modeBefore = (int)system._DefaultMode;
    }

    /// <summary>
    /// CHAT7: a line counts as sent when the chat loses focus with its input emptied (the game clears it when Enter
    /// sends). Escape on a typed line may count too; recall then offers it, which is harmless.
    /// </summary>
    private static void NoteSent(HUDChatWindow window)
    {
        bool focused = window.IsChatFocused;
        bool lost = SentWatch.LostFocus(focused);
        string typed = SentWatch.Typed;
        string now = window.ChatInputField?.text ?? "";
        string? sent = SentWatch.Frame(focused, now, ChatTabs.FocusLostToTabRow);
        // CHAT8: a /loc line the game just sent gets its location token after it; never on a tab click.
        if (lost && !SentWatch.LastLossWasTabClick) SendLocAfterGame(window, typed, now);
        if (sent != null)
        {
            EnsureRecallLoaded();
            Recall.Push(sent);
            SaveRecall();
        }

        if (lost) Recall.EndBrowsing();
    }

    private static string RecallPath() =>
        System.IO.Path.Combine(BepInEx.Paths.BepInExRootPath, "progenitor-cache", "chat", "sent.txt");

    private static void EnsureRecallLoaded()
    {
        if (_recallLoaded) return;
        _recallLoaded = true;
        try
        {
            string path = RecallPath();
            if (System.IO.File.Exists(path)) Recall.Load(System.IO.File.ReadAllLines(path));
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: the sent-line history could not be read: " + e.Message); }
    }

    private static void SaveRecall()
    {
        try
        {
            string path = RecallPath();
            System.IO.Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            var lines = new List<string>();
            foreach (var l in Recall.Lines) lines.Add(l.Replace('\n', ' ').Replace('\r', ' '));
            System.IO.File.WriteAllLines(path, lines);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: the sent-line history could not be saved: " + e.Message); }
    }

    private static bool LongAllowed => (_longMessages?.Value ?? true) && ChatPolicy.Current.AllowLongMessages;

    /// <summary>CHAT9: let the input field take a long line while long messages are on (the game's limit otherwise).</summary>
    private static void AllowLongInput(HUDChatWindow window)
    {
        var field = window.ChatInputField;
        if (field == null) return;
        if (_limitedField == null || _limitedField.Pointer != field.Pointer)
        {
            RestoreInput();
            _limitedField = field;
        }

        if (!LongAllowed) LongParts.Clear();
        if (_gameCharacterLimit < 0) _gameCharacterLimit = field.characterLimit;
        int want = LongAllowed ? ChatPolicy.ClampLongMessage(ChatPolicy.Current.MaxLongMessageChars) : _gameCharacterLimit;
        if (_gameCharacterLimit > 0 && want < _gameCharacterLimit) want = _gameCharacterLimit;
        if (field.characterLimit != want) field.characterLimit = want;
    }

    /// <summary>
    /// The chat-scope Enter: a line that is exactly <c>/loc</c> becomes the location token, which the game's own Enter
    /// then sends (CHAT8; <c>/loc </c> with a space already expanded as it was typed); otherwise a long line goes in parts.
    /// </summary>
    private static void OnSendKey()
    {
        try
        {
            var system = Worlds.Client?.GetExistingSystemManaged<ClientChatSystem>();
            var field = system?._ChatWindow?.ChatInputField;
            string line = field?.text ?? "";
            if (system != null && field != null && ChatInputTokens.TryExpandOnSend(line, out string token))
            {
                // Empty the input so the game's Enter sends nothing, and send the token ourselves (as long lines do).
                field.text = "";
                SentWatch.Forget();
                EnsureRecallLoaded();
                Recall.Push(line);
                SaveRecall();
                QueueLine(system, token);
                return;
            }
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: /loc on send threw: " + e.Message); }

        SendLong();
    }

    /// <summary>One line on the channel the input would have sent it to, through the long-part queue (in order).</summary>
    private static void QueueLine(ClientChatSystem system, string text)
    {
        LongParts.Enqueue((text, system._DefaultMode, system._WhisperUser));
        Plugin.Logger?.LogInfo("Chat: /loc sent as " + text + ".");
    }

    /// <summary>
    /// The game handled Enter on <c>/loc</c> before the send key ran (<see cref="ChatInputTokens.SendAfterGame"/>):
    /// send the location now.
    /// </summary>
    private static void SendLocAfterGame(HUDChatWindow window, string lineBefore, string lineNow)
    {
        try
        {
            var field = window.ChatInputField;
            bool canceled = field != null && field.wasCanceled;
            if (!ChatInputTokens.SendAfterGame(lineBefore, lineNow, canceled)) return;
            if (!ChatInputTokens.TryExpandOnSend(lineBefore, out string token)) return;
            var system = Worlds.Client?.GetExistingSystemManaged<ClientChatSystem>();
            if (system != null) QueueLine(system, token);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: /loc after send threw: " + e.Message); }
    }

    /// <summary>
    /// CHAT9: Enter on a line too long for one chat line: empty the input (so the game sends nothing) and send the
    /// parts, on the channel the line would have gone to. Commands and ordinary lines are the game's.
    /// </summary>
    private static void SendLong()
    {
        if (!LongAllowed) return;
        try
        {
            var world = Worlds.Client;
            var system = world?.GetExistingSystemManaged<ClientChatSystem>();
            var field = system?._ChatWindow?.ChatInputField;
            if (system == null || field == null) return;
            string text = field.text ?? "";
            if (text.Length == 0 || text[0] == '/' || text[0] == '.' || !ChatLongMessage.IsLong(text)) return;
            var parts = ChatLongMessage.Split(text, ChatPolicy.ClampLongMessage(ChatPolicy.Current.MaxLongMessageChars));
            field.text = "";
            SentWatch.Forget();
            EnsureRecallLoaded();
            Recall.Push(text);
            SaveRecall();
            var type = system._DefaultMode;
            var whisper = system._WhisperUser;
            foreach (var p in parts) LongParts.Enqueue((p, type, whisper));
            Plugin.Logger?.LogInfo("Chat: sending a long line in " + parts.Count + " parts.");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: long message threw: " + e.Message); }
    }

    private static void SendLongParts(World world)
    {
        if (LongParts.Count == 0 || ++_longPartFrame < 6) return;
        _longPartFrame = 0;
        var (text, type, whisper) = LongParts.Dequeue();
        try { ChatHelper.SendChatMessageOfType(world.EntityManager, text, type, whisper); }
        catch (Exception e)
        {
            LongParts.Clear();
            Plugin.Logger?.LogWarning("Chat: sending a long message part threw: " + e.Message);
        }
    }

    /// <summary>The recall keys (chat-scope binds, so they run only while typing in chat).</summary>
    private static void RecallStep(bool up)
    {
        if (!(_recall?.Value ?? true)) return;
        try
        {
            EnsureRecallLoaded();
            var world = Worlds.Client;
            var field = world?.GetExistingSystemManaged<ClientChatSystem>()?._ChatWindow?.ChatInputField;
            if (field == null) return;
            string? line = up ? Recall.Previous(field.text) : Recall.Next();
            if (line != null) SetLine(field, line);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: recall threw: " + e.Message); }
    }

    private static ConfigEntry<bool>? _channelColor;
    private static ConfigEntry<bool>? _commandHints;
    private static ConfigEntry<bool>? _modSuggest;
    private static ConfigEntry<string>? _testNames;

    // ---- CHAT10: command hints while typing "." ------------------------------------------------------------
    private const int MaxHints = 3;
    private static GameObject? _hintRoot;
    private static TMPro.TextMeshProUGUI? _hintText;
    private static IntPtr _hintField;
    private static string _hintFor = "\u0001";
    private static int _commandIndex = -1;
    private static string _commandTyped = "";
    private static string _commandResult = "";

    /// <summary>
    /// The hint panel above the input line: the server's commands that match what is typed (command, usage, help), up
    /// to three. Hidden when the line does not start with "." or the server sends no hints.
    /// </summary>
    private static void UpdateHints(HUDChatWindow window, string text)
    {
        double now = HintClock.Elapsed.TotalSeconds;
        // The same line keeps its panel; names and mods can change, so it is redrawn once a second too.
        if (string.Equals(text, _hintFor, StringComparison.Ordinal) && now < _hintRefreshAt)
        {
            if (_hintShown && _hintRoot != null && _hintRoot.Pointer != IntPtr.Zero && !_hintRoot.activeSelf) _hintRoot.SetActive(true);
            return;
        }

        string? panel = HintText(text);
        _hintFor = text;
        _hintRefreshAt = now + 1.0;
        _hintShown = panel != null;
        if (panel == null)
        {
            if (_hintRoot != null && _hintRoot.Pointer != IntPtr.Zero && _hintRoot.activeSelf) _hintRoot.SetActive(false);
            return;
        }

        if (!EnsureHintPanel(window)) return;
        _hintText!.text = panel;
        FitHintPanel(panel);
        if (!_hintRoot!.activeSelf) _hintRoot.SetActive(true);
    }

    private static readonly System.Diagnostics.Stopwatch HintClock = System.Diagnostics.Stopwatch.StartNew();
    private static double _hintRefreshAt;
    private static bool _hintShown;

    /// <summary>
    /// What the panel shows for the line, or null: the server's commands after ".", mods after an unfinished
    /// <c>[mod:</c> (CHAT11), or online players after <c>/w</c> (CHAT2). The first row names the key that completes it.
    /// </summary>
    private static string? HintText(string text)
    {
        var sb = new System.Text.StringBuilder();
        string tab = " <color=" + ChatFormat.MutedHex + ">· " + UiRichText.Literal(CompletionKeyName()) + "</color>";
        if (text.Length > 0 && text[0] == '.')
        {
            if (!(_commandHints?.Value ?? true)) return null;
            ChatCommandHints.RequestIfNeeded();
            if (!ChatCommandHints.Available) return null;
            var matches = ChatCommandHints.Match(text, MaxHints);
            if (matches.Count == 0) return null;
            for (int i = 0; i < matches.Count; i++)
            {
                var h = matches[i];
                if (i > 0) sb.Append('\n');
                sb.Append("<b>").Append(UiRichText.Literal(h.Command)).Append("</b>");
                if (!string.IsNullOrWhiteSpace(h.Usage)) sb.Append(' ').Append("<color=").Append(ChatFormat.MutedHex).Append('>').Append(UiRichText.Literal(h.Usage)).Append("</color>");
                if (!string.IsNullOrWhiteSpace(h.Help)) sb.Append("  ").Append(UiRichText.Literal(h.Help));
            }

            return sb.ToString();
        }

        if (ModSuggestOn && ChatModSuggest.TryParse(text, out _, out string partial))
        {
            var mods = ChatModSuggest.Rank(partial, ModsMenu.ModLinkSuggestions(partial), MaxHints);
            if (mods.Count == 0)
            {
                sb.Append("<color=").Append(ChatFormat.MutedHex).Append(">No mod matches \"").Append(UiRichText.Literal(partial)).Append("\".");
                if (!ModsMenu.ModCatalogLoaded()) sb.Append(" Open the Mods menu once to search Thunderstore too.");
                return sb.Append("</color>").ToString();
            }

            for (int i = 0; i < mods.Count; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append("<b>").Append(UiRichText.Literal(mods[i].Label)).Append("</b> <color=").Append(ChatFormat.MutedHex).Append('>')
                    .Append(UiRichText.Literal(mods[i].Package)).Append(mods[i].Installed ? " · installed" : "").Append("</color>");
                if (i == 0) sb.Append(tab);
            }

            return sb.ToString();
        }

        if (NameCompletion && ChatCommandLogic.TryParseWhisperName(text, out _, out string name))
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return null;
            var names = ChatCommandLogic.Matches(name, OnlineNames(world, out _), self: null);
            if (names.Count == 0)
                return "<color=" + ChatFormat.MutedHex + ">No player online named \"" + UiRichText.Literal(name) + "\".</color>";
            for (int i = 0; i < names.Count && i < MaxHints; i++)
            {
                if (i > 0) sb.Append('\n');
                sb.Append("<b>").Append(UiRichText.Literal(names[i])).Append("</b>");
                if (i == 0) sb.Append(tab);
            }

            if (names.Count > MaxHints) sb.Append(" <color=").Append(ChatFormat.MutedHex).Append(">+").Append(names.Count - MaxHints).Append(" more</color>");
            return sb.ToString();
        }

        return null;
    }

    private static bool ModSuggestOn => (_modSuggest?.Value ?? true) && ChatPolicy.Current.AllowModLinks;

    /// <summary>The completion key as the player sees it ("Tab").</summary>
    private static string CompletionKeyName()
    {
        try
        {
            var key = _completeKey;
            if (key == null || key.IsDisposed) return "Tab";
            string shown = key.Chord.DisplayText;
            return string.IsNullOrWhiteSpace(shown) ? "Tab" : shown;
        }
        catch { return "Tab"; }
    }

    private static int _modIndex = -1;
    private static string _modTyped = "";
    private static string _modResult = "";

    /// <summary>CHAT11: the completion key after an unfinished <c>[mod:</c>: the first suggestion, then the next on each press.</summary>
    private static string? CompleteMod(string text)
    {
        if (!ModSuggestOn) return null;
        bool cycling = _modIndex >= 0 && string.Equals(text, _modResult, StringComparison.Ordinal);
        if (!cycling)
        {
            if (!ChatModSuggest.TryParse(text, out _, out _)) return null;
            _modTyped = text;
            _modIndex = -1;
        }

        if (!ChatModSuggest.TryParse(_modTyped, out int start, out string partial)) return null;
        var mods = ChatModSuggest.Rank(partial, ModsMenu.ModLinkSuggestions(partial), 10);
        if (mods.Count == 0)
        {
            _modIndex = -1;
            return null;
        }

        _modIndex = (_modIndex + 1) % mods.Count;
        _modResult = ChatModSuggest.Complete(_modTyped, start, mods[_modIndex].Package);
        return _modResult;
    }

    private static bool ModWouldComplete(string text) =>
        ModSuggestOn && ((_modIndex >= 0 && string.Equals(text, _modResult, StringComparison.Ordinal))
            || ChatModSuggest.TryParse(text, out _, out _));

    private static bool EnsureHintPanel(HUDChatWindow window)
    {
        var field = window.ChatInputField;
        if (field == null || window.ChatMessagePrefab == null) return false;
        if (_hintRoot != null && _hintRoot.Pointer != IntPtr.Zero && _hintField == field.Pointer) return _hintText != null;
        DestroyHints();
        var root = new GameObject("progenitor-chat-hints");
        var rect = root.AddComponent<RectTransform>();
        root.transform.SetParent(field.transform, false);
        root.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
        rect.anchorMin = new Vector2(0f, 1f);
        rect.anchorMax = new Vector2(1f, 1f);
        rect.pivot = new Vector2(0f, 0f);
        rect.anchoredPosition = new Vector2(0f, 2f);
        rect.sizeDelta = new Vector2(0f, 66f);
        // Its own canvas, sorted above the chat window: the message log comes after the input in the window, so without
        // this the log's lines draw over the panel (live 2026-10-07).
        var canvas = root.AddComponent<Canvas>();
        canvas.overrideSorting = true;
        var parentCanvas = field.GetComponentInParent<Canvas>();
        canvas.sortingOrder = (parentCanvas != null ? parentCanvas.sortingOrder : 0) + 5;
        var bg = root.AddComponent<UnityEngine.UI.Image>();
        bg.color = new Color(0.05f, 0.05f, 0.07f, 0.96f);
        bg.raycastTarget = false;
        var line = UnityEngine.Object.Instantiate(window.ChatMessagePrefab.gameObject, root.transform)?.TryCast<GameObject>();
        var text = line?.GetComponent<TMPro.TextMeshProUGUI>();
        if (line == null || text == null)
        {
            UnityEngine.Object.Destroy(root);
            return false;
        }

        line.AddComponent<UnityEngine.UI.LayoutElement>().ignoreLayout = true;
        var lr = line.transform.TryCast<RectTransform>();
        if (lr != null)
        {
            lr.anchorMin = Vector2.zero;
            lr.anchorMax = Vector2.one;
            lr.offsetMin = new Vector2(6f, 2f);
            lr.offsetMax = new Vector2(-6f, -2f);
        }

        text.richText = true;
        text.color = Color.white;
        text.raycastTarget = false;
        // One row per line: a long row ends in "…" rather than wrapping past the panel.
        text.enableWordWrapping = false;
        text.overflowMode = TMPro.TextOverflowModes.Ellipsis;
        _hintRoot = root;
        _hintText = text;
        _hintField = field.Pointer;
        _hintFor = "\u0001";
        return true;
    }

    /// <summary>The panel is as tall as its lines (one per row), so three rows never spill over its edge.</summary>
    private static void FitHintPanel(string panel)
    {
        var rect = _hintRoot?.transform.TryCast<RectTransform>();
        if (rect == null || _hintText == null) return;
        int lines = 1;
        foreach (char c in panel) if (c == '\n') lines++;
        float lineHeight = Math.Max(16f, _hintText.fontSize * 1.3f);
        rect.sizeDelta = new Vector2(0f, lines * lineHeight + 8f);
    }

    private static void DestroyHints()
    {
        try { if (_hintRoot != null && _hintRoot.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_hintRoot); }
        catch { /* gone with its window */ }
        _hintRoot = null;
        _hintText = null;
        _hintField = IntPtr.Zero;
        _hintFor = "\u0001";
    }

    /// <summary>Tab on a "." line: the first matching command, then the next on each press of the same line.</summary>
    private static string? CompleteCommand(string text)
    {
        if (!(_commandHints?.Value ?? true) || !ChatCommandHints.Available) return null;
        bool cycling = _commandIndex >= 0 && string.Equals(text, _commandResult, StringComparison.Ordinal);
        if (!cycling)
        {
            if (text.Length == 0 || text[0] != '.') return null;
            _commandTyped = text;
            _commandIndex = -1;
        }

        var matches = ChatCommandHints.Match(_commandTyped, 10);
        if (matches.Count == 0)
        {
            _commandIndex = -1;
            return null;
        }

        _commandIndex = (_commandIndex + 1) % matches.Count;
        _commandResult = matches[_commandIndex].Command + " ";
        return _commandResult;
    }

    private static bool CommandWouldComplete(string text) =>
        (_commandHints?.Value ?? true) && ChatCommandHints.Available
        && ((_commandIndex >= 0 && string.Equals(text, _commandResult, StringComparison.Ordinal))
            || (text.Length > 0 && text[0] == '.' && ChatCommandHints.Match(text, 1).Count > 0));
    private static int _coloredMode = -1;
    private static string _coloredWith = "";
    private static Color _gameInputColor;
    private static bool _gameInputColorKnown;

    /// <summary>
    /// CHAT6: the line being typed takes its channel's color (the game's chat colors: Global, Clan, Whisper, Local,
    /// System), so the channel shows while typing, not only in the empty line's placeholder.
    /// </summary>
    private static void ColorInput(ClientChatSystem system, HUDChatWindow window)
    {
        var text = window.ChatInputField?.textComponent;
        var colors = window.Colors;
        if (text == null || colors == null) return;
        if (!_gameInputColorKnown)
        {
            _gameInputColor = text.color;
            _gameInputColorKnown = true;
        }

        int mode = (int)system._DefaultMode;
        bool on = _channelColor?.Value ?? true;
        string colorsNow = ChatView.Colors?.Value ?? "";
        if (on && mode == _coloredMode && string.Equals(colorsNow, _coloredWith, StringComparison.Ordinal)) return;
        if (!on)
        {
            if (_coloredMode >= 0) text.color = _gameInputColor;
            _coloredMode = -1;
            return;
        }

        _coloredMode = mode;
        _coloredWith = colorsNow;
        // The game's ChatMessageType: 0 Global, 1 Region, 2 Team, 3 Whisper, 4 Local, 5 System.
        var kind = mode switch
        {
            0 => ChatKind.Global,
            2 => ChatKind.Team,
            3 => ChatKind.WhisperTo,
            4 => ChatKind.Local,
            5 => ChatKind.System,
            _ => ChatKind.Unknown,
        };
        string? own = ChatColors.HexFor(colorsNow, kind);
        if (own != null && ColorUtility.TryParseHtmlString(own, out var mine))
        {
            text.color = mine;
            return;
        }

        text.color = mode switch
        {
            0 => colors.GlobalText,
            2 => colors.TeamText,
            3 => colors.WhisperText,
            4 => colors.LocalText,
            5 => colors.SystemText,
            _ => _gameInputColor,
        };
    }

    /// <summary>The game's channel cycle is hard-wired to Tab; it only needs undoing when the completion key is plain Tab.</summary>
    private static bool CompletionKeyIsTab()
    {
        var key = _completeKey;
        if (key == null || key.IsDisposed) return false;
        var chord = key.Chord;
        return chord.Key == UiKey.Tab && chord.Modifiers == KeyModifiers.None;
    }

    /// <summary>
    /// CHAT2: after the game's chat update. If it cycled the channel while a Tab press was completing a name, put the
    /// channel and its placeholder back, so one press does one thing.
    /// </summary>
    private static void AfterChatUpdate(SystemHookContext ctx)
    {
        int before = _modeBefore;
        _modeBefore = -1;
        if (before < 0) return;
        try
        {
            var world = ctx?.World;
            if (world == null || !world.IsCreated) return;
            var system = world.GetExistingSystemManaged<ClientChatSystem>();
            if (system == null || system.Pointer == IntPtr.Zero) return;
            if ((int)system._DefaultMode == before) return;
            // Only when this Tab press has a name to complete: with no match Tab keeps its game meaning.
            var field = system._ChatWindow?.ChatInputField;
            string now = field?.text ?? "";
            if (field == null || !(Completer.WouldComplete(now, OnlineNames(world, out string? self), self)
                || CommandWouldComplete(now) || ModWouldComplete(now))) return;
            system._DefaultMode = (ChatMessageType)before;
            var window = system._ChatWindow;
            if (window != null) window.SetPlaceholderMode((ChatMessageType)before, system._WhisperUserName ?? "");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: channel restore after a name completion threw: " + e.Message);
        }
    }

    /// <summary>The completion key (a <see cref="KeybindScope.Chat"/> bind, so it runs only while the chat has focus).</summary>
    private static void CompleteName()
    {
        try
        {
            var world = Worlds.Client;
            if (world == null || !world.IsCreated) return;
            var system = world.GetExistingSystemManaged<ClientChatSystem>();
            var window = system?._ChatWindow;
            var field = window?.ChatInputField;
            if (field == null) return;
            string current = field.text ?? "";
            string? line = current.Length > 0 && current[0] == '.'
                ? CompleteCommand(current)
                : CompleteMod(current)
                  ?? (NameCompletion ? Completer.Complete(current, OnlineNames(world, out string? self), self) : null);
            if (line != null) SetLine(field, line);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: name completion threw: " + e.Message);
        }
    }

    private static void SetLine(TMPro.TMP_InputField field, string line)
    {
        field.text = line;
        field.MoveTextEnd(false);
    }

    /// <summary>
    /// Connected players' character names from the client world's <c>User</c> entities (the read Core already
    /// uses elsewhere). This player's own name is not filtered out: finding the local user would need a new generic
    /// instantiation over a game type, and whispering yourself only shows the game's own answer.
    /// </summary>
    private static List<string> OnlineNames(World world, out string? self)
    {
        self = null;
        var names = new List<string>();
        var em = world.EntityManager;
        foreach (var e in Query.Entities(em, false, false, ComponentType.ReadOnly<User>()))
        {
            User user;
            try { user = em.GetComponentData<User>(e); }
            catch { continue; }
            if (!user.IsConnected) continue;
            string name = user.CharacterName.ToString();
            if (name.Length == 0) continue;
            names.Add(name);
        }

        // [Chat] TestNames: names for testing the suggestions with no other players online.
        string extra = _testNames?.Value ?? "";
        if (extra.Length > 0)
            foreach (var n in extra.Split(new[] { ',', ';' }, StringSplitOptions.RemoveEmptyEntries))
                if (n.Trim().Length > 0 && !names.Contains(n.Trim())) names.Add(n.Trim());

        return names;
    }

    /// <summary>
    /// The order-97 context stays registered whatever <c>ClickSafe</c> says: it also carries the wheel guard (the
    /// mouse wheel over the box scrolls the chat and never zooms the camera), which has no setting. With
    /// <c>ClickSafe</c> off the buttons half takes nothing.
    /// </summary>
    private static void UpdateClickGuard(World world, bool overChat)
    {
        if (_frame >= _retryFrame)
        {
            InputActionSystem? input = null;
            try { input = InputHelper.GetInputActionSystem(); } catch { /* */ }
            bool live = GameInputBlocker.ChatGuardLive(input, verify: true) || GameInputBlocker.RegisterChatGuard(world);
            _retryFrame = _frame + (live ? VerifyFrames : RegisterRetryFrames);
        }

        if (_frame - _bindingsFrame >= BindingsRefreshFrames)
        {
            _bindingsFrame = _frame;
            RefreshBindings();
        }

        ChatClickGuard.UpdateWheel(overChat);
        ChatClickGuard.Update(ClickSafe, overChat, UnityInputRuntime.AnyMouseButtonHeld());
    }

    /// <summary>The game's actions bound to the middle, back and forward buttons, from the keybind snapshot.</summary>
    private static void RefreshBindings()
    {
        try
        {
            var snapshot = KeybindArbiter.GameSnapshot();
            if (snapshot == null || !snapshot.Available)
            {
                ChatClickGuard.Use(null);
                return;
            }

            var bound = new List<(string, UiKey)>();
            foreach (var claim in snapshot.Claims)
            {
                if (claim.Source != KeybindSource.Game) continue;
                bound.Add((claim.ActionId, claim.Chord.Key));
            }

            ChatClickGuard.Use(bound);
        }
        catch (Exception e)
        {
            ChatClickGuard.Use(null);
            Plugin.Logger?.LogWarning("Chat: game mouse bindings could not be read: " + e.Message);
        }
    }
}
#endif
