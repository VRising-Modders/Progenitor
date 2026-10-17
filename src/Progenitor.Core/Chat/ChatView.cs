#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx.Configuration;
using Progenitor.Ecs;
using Progenitor.Inputs;
using Progenitor.Ui;
using ProjectM.Network;
using ProjectM.UI;
using TMPro;
using Unity.Entities;
using UnityEngine;

namespace Progenitor.Chat;

/// <summary>
/// CHAT5: Progenitor's own chat view inside the game's chat box. The game draws at most 50 pooled lines across all
/// channels; Progenitor keeps its own <see cref="ChatLog"/> per server and draws the active tab's newest lines.
///
/// <para><b>How it sits in the game's window.</b> The game's lines live under <c>HUDChatWindow.TextNode</c> ("Content",
/// a vertical layout with a size fitter under the masked Viewport of the <c>ScrollRect</c>). The view clones that node
/// (layout and all) as a sibling, empties it, fills it with lines made from the game's own line prefab
/// (<c>ChatMessagePrefab</c>, so the font, size and wrapping match), points the <c>ScrollRect</c> at it and hides the
/// game's node. The game keeps writing its own lines into the hidden node; nothing of it is changed. Detaching puts the
/// game's node and the scroll content back and destroys the clone. The window's fade and position are the game's, so
/// the view fades and moves with it.</para>
///
/// <para><b>What a line shows.</b> <see cref="ChatFormat"/>: the game's own prefix (channel tag, colored name), the
/// message with links, a repeat count, an optional timestamp, the mention highlight; restored lines from an earlier
/// session are dimmed, and dividers mark where a session started and ended.</para>
///
/// <para><b>Clicks</b> (over the chat log; the click guard keeps them from the game): left on a link opens it (a web
/// link asks first; a mod link opens the Mods menu); right on a line copies it.</para>
/// </summary>
internal static class ChatView
{
    internal const string CloneName = "progenitor-chat-content";

    // ---- settings (bound by ChatWindow) ------------------------------------------------------------------
    internal static ConfigEntry<bool>? ViewEnabled;
    internal static ConfigEntry<bool>? Timestamps;
    internal static ConfigEntry<bool>? CollapseRepeats;
    internal static ConfigEntry<bool>? KeepHistory;
    internal static ConfigEntry<int>? HistoryLines;
    internal static ConfigEntry<bool>? Mentions;
    internal static ConfigEntry<bool>? MentionSound;
    internal static ConfigEntry<string>? MentionWords;
    internal static ConfigEntry<int>? FontSize;

    /// <summary>The mention sound: the game's short positive UI chirp, distinct from the whisper sound.</summary>
    private const ProjectM.UISoundType MentionChirp = ProjectM.UISoundType.UI_Generic_Feedback_Positive;

    /// <summary><c>[Chat] Colors</c>: the player's channel colors (<see cref="ChatColors"/>).</summary>
    internal static ConfigEntry<string>? Colors;
    internal static ConfigEntry<string>? MutedPlayers;

    internal const int DefaultVisibleLines = 120;

    /// <summary>The mod-link opener (package, version) → status; set by the mod-link module.</summary>
    internal static Func<string, string?, string>? OpenModLink { get; set; }

    private static readonly ChatLog Log = new();
    private static readonly List<ChatLineSlot> Lines = new();
    private static ChatTab _tab = ChatTab.All();

    private static IntPtr _windowPtr;
    private static HUDChatWindow? _window;
    private static GameObject? _clone;
    private static Transform? _gameNode;
    private static int _drawnVersion = -1;
    private static ChatTab? _drawnTab;
    private static string _host = "";
    private static string _serverName = "";
    private static string _ownName = "";
    private static bool _restored;
    private static bool _warned;
    private static int _saveFrame;
    private static bool _dirtySave;

    private sealed class ChatLineSlot
    {
        internal GameObject Go = null!;
        internal float BaseSize;
        internal TextMeshProUGUI Text = null!;
        internal ChatRecord? Record;
        internal List<ChatSpan> Spans = new();
    }

    internal static bool On => (ViewEnabled?.Value ?? true) && !ReplacedByAnotherMod();

    /// <summary><c>[Chat] ReplacedBy</c>: plugin GUIDs or names that replace the game's chat window (<see cref="ChatReplacement"/>).</summary>
    internal static ConfigEntry<string>? ReplacedBy;

    private static string? _replacedFor;
    private static bool _replacedByListed;

    /// <summary>Set for the session once another mod was seen driving the chat window's lines.</summary>
    private static bool _takenOver;

    /// <summary>
    /// Whether a mod that replaces the game's chat window is loaded: one listed in <c>[Chat] ReplacedBy</c> (checked
    /// again when the setting changes), or one seen driving the window's lines at attach. Then Progenitor's view stays
    /// off, so the two never fight over the same window; the other features (visibility, click guard, commands) work on
    /// whatever window the game has.
    /// </summary>
    private static bool ReplacedByAnotherMod()
    {
        if (_takenOver) return true;
        string setting = ReplacedBy?.Value ?? "";
        if (_replacedFor == setting) return _replacedByListed;
        _replacedFor = setting;
        _replacedByListed = false;
        if (ChatReplacement.Entries(setting).Count == 0) return false;
        try
        {
            var plugins = BepInEx.Unity.IL2CPP.IL2CPPChainloader.Instance?.Plugins;
            if (plugins == null) return false;
            var pairs = new List<KeyValuePair<string, string>>();
            foreach (var pair in plugins) pairs.Add(new KeyValuePair<string, string>(pair.Key ?? "", pair.Value?.Metadata?.Name ?? ""));
            string? found = ChatReplacement.FirstListed(pairs, setting);
            if (found == null) return false;
            _replacedByListed = true;
            Plugin.Logger?.LogInfo("Chat: " + found + " replaces the chat window ([Chat] ReplacedBy); Progenitor's chat view stays off.");
            return true;
        }
        catch { /* the loader's table is evidence, never a gate */ }

        return false;
    }

    /// <summary>The tab the view draws (set by the tab strip).</summary>
    internal static ChatTab ActiveTab
    {
        get => _tab;
        set
        {
            _tab = value ?? ChatTab.All();
            _drawnVersion = -1;
        }
    }

    internal static ChatLog CurrentLog => Log;

    private static ChatFormat.Options Options() => new()
    {
        Timestamps = Timestamps?.Value ?? false,
        Links = ChatPolicy.Current.AllowLinks,
        ModLinks = ChatPolicy.Current.AllowModLinks,
        Mentions = Mentions?.Value ?? true,
    };

    // ---- world lifetime ------------------------------------------------------------------------------------

    /// <summary>A new client world: save the old server's log, start this server's (its history restored on attach).</summary>
    internal static void OnWorldChanged()
    {
        Leave();
        Detach();
        Log.Clear();
        _restored = false;
        _ownName = "";
        _host = "";
        _serverName = "";
        _continued = false;
        RestoredTail.Clear();
    }

    /// <summary>Core unload or the view turned off: save, mark the session end, put the game's view back.</summary>
    internal static void Uninstall()
    {
        // A Core swap or unload is not leaving the server: save without the divider, so the next generation continues
        // the same session instead of showing "Left" and "Joined".
        Leave(marker: false);
        Detach();
        Log.Clear();
        _restored = false;
    }

    private static void Leave(bool marker = true)
    {
        if (!_restored || _host.Length == 0) return;
        try
        {
            if (marker) Log.AddMarker(ChatLog.LeftText(DateTime.Now), DateTime.UtcNow);
            Save();
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: saving the chat history threw: " + e.Message); }
    }

    private static string HistoryPath(string host)
    {
        string root = BepInEx.Paths.BepInExRootPath;
        return Path.Combine(root, "progenitor-cache", "chat", ChatLog.FileKey(host) + ".jsonl");
    }

    private static void RestoreHistory()
    {
        if (_restored) return;
        _restored = true;
        try { _host = Networking.ServerModPause.CurrentHost() ?? ""; } catch { _host = ""; }
        try { _serverName = Networking.ServerModPause.CurrentServerName() ?? ""; } catch { _serverName = ""; }
        int keep = ChatLog.ClampCapacity(HistoryLines?.Value ?? ChatLog.DefaultCapacity);
        Log.Capacity = keep;
        if ((KeepHistory?.Value ?? true) && _host.Length > 0)
        {
            try
            {
                string path = HistoryPath(_host);
                if (File.Exists(path))
                {
                    var restored = ChatLog.Deserialize(File.ReadAllText(path), keep);
                    RestoredTail.Clear();
                    for (int i = Math.Max(0, restored.Count - 60); i < restored.Count; i++)
                        if (restored[i].Kind != ChatKind.Session) RestoredTail.Add(TailKey(restored[i].Kind, restored[i].User, restored[i].Text));
                    // Saved by an unload (no "Left" after the last "Joined"): the same session goes on.
                    _continued = restored.Count > 0 && LastDividerIsJoined(restored);
                    Log.Restore(restored);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("Chat: the chat history could not be read: " + e.Message); }
        }

        if (!_continued) Log.AddMarker(ChatLog.JoinedText(_serverName, DateTime.Now), DateTime.UtcNow);
    }

    /// <summary>The saved lines' last dividers, newest last, from an earlier generation of this same session.</summary>
    private static readonly HashSet<string> RestoredTail = new();
    private static bool _continued;

    private static string TailKey(ChatKind kind, string user, string text) => ((int)kind) + "|" + user + "|" + text;

    private static bool LastDividerIsJoined(List<ChatRecord> restored)
    {
        for (int i = restored.Count - 1; i >= 0; i--)
        {
            if (restored[i].Kind != ChatKind.Session) continue;
            return restored[i].Text.StartsWith("Joined ", StringComparison.Ordinal);
        }

        return false;
    }

    private static void Save()
    {
        _dirtySave = false;
        if (!(KeepHistory?.Value ?? true) || _host.Length == 0) return;
        string path = HistoryPath(_host);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, Log.Serialize(Log.Capacity));
        File.Copy(tmp, path, overwrite: true);
        File.Delete(tmp);
    }

    // ---- per frame (from ChatWindow's hook, before the game's chat update) ------------------------------

    /// <summary>A line the game added: into the log (with its mention check and sound).</summary>
    internal static void Ingest(ChatEntry entry, World world, HUDChatWindow? window)
    {
        if (!On || entry == null) return;
        RestoreHistory();
        var kind = (ChatKind)(int)entry.MessageType;
        if (!Enum.IsDefined(typeof(ChatKind), kind)) kind = ChatKind.Unknown;
        bool self = entry.FromType == MessageFromType.Self;
        string user = entry.User ?? "";
        if (user == "?") user = "";
        string text = entry.MessageText ?? "";
        // Provenance from the game's event, never the text: the server fills the sender's network id in itself.
        bool hasSender;
        try { hasSender = entry.From.Type != NetworkIdType.None; }
        catch { hasSender = true; }
        if (self && user.Length > 0) _ownName = user;
        // After a Core swap the game's own lines (read once on attach, window == null) are already in the restored log.
        if (window == null && _continued && RestoredTail.Contains(TailKey(kind, user, text))) return;
        // CHAT7: a muted player's lines stay out of the view (the game's hidden lines still have them).
        if (!self && user.Length > 0 && ChatMute.Contains(ChatMute.Parse(MutedPlayers?.Value), user)) return;

        bool mention = false;
        if ((Mentions?.Value ?? true) && kind != ChatKind.System && kind != ChatKind.Lore)
        {
            string own = OwnName(world);
            bool admin = text.IndexOf(ChatMentions.AtAll, StringComparison.OrdinalIgnoreCase) >= 0 && IsAdmin(world, user);
            mention = ChatMentions.IsMention(text, own, ChatMentions.ParseWords(MentionWords?.Value), admin,
                ChatPolicy.Current.AllowAtAll);
        }

        var fresh = new ChatRecord
        {
            Kind = kind,
            FromSelf = self,
            User = user,
            Text = text,
            Full = entry.FullText ?? "",
            TimeUtc = DateTime.UtcNow,
            Mention = mention,
            Source = ChatRecord.SourceOf(kind, hasSender),
        };
        // CHAT9: a long message's later part joins its line (when the server allows long messages).
        var record = (ChatPolicy.Current.AllowLongMessages ? Log.TryJoin(fresh) : null)
            ?? Log.Add(fresh, collapse: CollapseRepeats?.Value ?? true);
        _dirtySave = true;

        if (mention && record.Repeat == 1 && (MentionSound?.Value ?? true) && window != null)
        {
            try { ProjectM.Audio.UIAudio.Play(window.SoundMapping, MentionChirp, "progenitor-chat-mention", window.gameObject); }
            catch { /* a missing sound costs the sound only */ }
        }

        ChatTabs.NoteNew(record);
    }

    /// <summary>This player's character name: from their own lines, else the only connected user.</summary>
    private static string OwnName(World world)
    {
        if (_ownName.Length > 0) return _ownName;
        try
        {
            var em = world.EntityManager;
            string only = "";
            int connected = 0;
            foreach (var e in Query.Entities(em, false, false, ComponentType.ReadOnly<User>()))
            {
                var u = em.GetComponentData<User>(e);
                if (!u.IsConnected) continue;
                connected++;
                only = u.CharacterName.ToString();
            }

            if (connected == 1) return only;
        }
        catch { /* unknown: no name mention until the player speaks */ }

        return "";
    }

    /// <summary>Whether the connected user named <paramref name="name"/> is a server admin (for <c>@all</c>).</summary>
    private static bool IsAdmin(World world, string name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        try
        {
            var em = world.EntityManager;
            foreach (var e in Query.Entities(em, false, false, ComponentType.ReadOnly<User>()))
            {
                var u = em.GetComponentData<User>(e);
                if (u.IsConnected && string.Equals(u.CharacterName.ToString(), name, StringComparison.Ordinal)) return u.IsAdmin;
            }
        }
        catch { /* unknown: not an admin */ }

        return false;
    }

    /// <summary>Attach to this window when needed, redraw when the log or tab changed, handle clicks, save now and then.</summary>
    internal static void Update(HUDChatWindow window, bool chatShown, bool pointerOverLog, RectTransform? hudParent)
    {
        try
        {
            if (!On)
            {
                if (_clone != null) Detach();
                return;
            }

            RestoreHistory();
            if (!Attached(window) && !Attach(window)) return;
            ChatTabs.Update(window, chatShown, hudParent);
            // Item names the game could not give yet (restored history drawn before its item data loaded) are retried;
            // one that resolves raises the names version in OptionsKey, so the lines redraw with the name.
            ChatGameData.PollItemNames();
            int optionsKey = OptionsKey();
            if (_drawnVersion != Log.Version || !ReferenceEquals(_drawnTab, _tab) || optionsKey != _drawnOptions) Redraw(optionsKey);
            KeepFontSize();
            EndFlash(now: false);
            if (chatShown && pointerOverLog)
            {
                HandleClicks();
                HandleHover();
            }
            else EndHover();
            if (_dirtySave && ++_saveFrame >= 600)
            {
                _saveFrame = 0;
                Save();
            }
        }
        catch (Exception e)
        {
            if (_warned) return;
            _warned = true;
            Plugin.Logger?.LogWarning("Chat: the chat view threw (logged once): " + e);
        }
    }

    private static bool Attached(HUDChatWindow window) =>
        _clone != null && _windowPtr == window.Pointer && _clone.Pointer != IntPtr.Zero;

    private static bool Attach(HUDChatWindow window)
    {
        Detach();
        var node = window.TextNode;
        var scroll = window.ScrollView;
        if (node == null || scroll == null || window.ChatMessagePrefab == null) return false;
        IntPtr content = scroll.content == null ? IntPtr.Zero : scroll.content.Pointer;
        if (ChatReplacement.ContentTakenOver(content, node.Pointer, IntPtr.Zero))
        {
            _takenOver = true;
            Plugin.Logger?.LogInfo("Chat: another mod drives the chat window's lines; Progenitor's chat view stays off.");
            return false;
        }

        var copy = UnityEngine.Object.Instantiate(node.gameObject, node.parent);
        var go = copy?.TryCast<GameObject>();
        if (go == null) return false;
        go.name = CloneName;
        for (int i = go.transform.childCount - 1; i >= 0; i--)
            UnityEngine.Object.Destroy(go.transform.GetChild(i).gameObject);
        _clone = go;
        _gameNode = node;
        _window = window;
        _windowPtr = window.Pointer;
        scroll.content = go.transform.TryCast<RectTransform>();
        node.gameObject.SetActive(false);
        Lines.Clear();
        _drawnVersion = -1;
        Plugin.Logger?.LogInfo("Chat: Progenitor's chat view attached (" + Log.Records.Count + " lines in the log).");
        return true;
    }

    /// <summary>Put the game's view back: its node, the scroll content, and destroy the clone.</summary>
    internal static void Detach()
    {
        EndFlash(now: true);
        EndHover();
        try
        {
            if (_gameNode != null && _gameNode.Pointer != IntPtr.Zero)
            {
                _gameNode.gameObject.SetActive(true);
                if (_window != null && _window.Pointer != IntPtr.Zero && _window.ScrollView != null)
                    _window.ScrollView.content = _gameNode.TryCast<RectTransform>();
            }

            if (_clone != null && _clone.Pointer != IntPtr.Zero) UnityEngine.Object.Destroy(_clone);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: detaching the chat view threw: " + e.Message); }

        _clone = null;
        _gameNode = null;
        _window = null;
        _windowPtr = IntPtr.Zero;
        Lines.Clear();
        _drawnVersion = -1;
        ChatTabs.Detach();
    }

    private static int _drawnOptions = int.MinValue;

    /// <summary>
    /// The game's line prefab carries a text-size scaler that sets the game's size when a line first shows, after the
    /// redraw set ours; so a chosen size is re-applied each frame (a float compare per visible line).
    /// </summary>
    private static void KeepFontSize()
    {
        int size = FontSize?.Value ?? 0;
        if (size <= 0) return;
        for (int i = 0; i < Lines.Count; i++)
        {
            var slot = Lines[i];
            if (slot.Record == null) continue;
            if (Math.Abs(slot.Text.fontSize - size) > 0.01f) slot.Text.fontSize = size;
        }
    }

    /// <summary>What a redraw depends on besides the log and the tab: the display settings and the server's policy.</summary>
    private static int OptionsKey()
    {
        var p = ChatPolicy.Current;
        int key = FontSize?.Value ?? 0;
        key = key * 2 + ((Timestamps?.Value ?? false) ? 1 : 0);
        key = key * 2 + ((Mentions?.Value ?? true) ? 1 : 0);
        key = key * 2 + (p.AllowLinks ? 1 : 0);
        key = key * 2 + (p.AllowModLinks ? 1 : 0);
        key = key * 31 + StringComparer.Ordinal.GetHashCode(Colors?.Value ?? "");
        key = key * 31 + ChatLinkRegistry.Version;
        key = key * 31 + ChatGameData.ItemNamesVersion;
        return key;
    }

    private static void Redraw(int optionsKey)
    {
        if (_clone == null || _window == null) return;
        var scroll = _window.ScrollView;
        bool atBottom = scroll == null || scroll.verticalNormalizedPosition <= 0.02f;
        var visible = Log.Visible(_tab, DefaultVisibleLines);
        var options = Options();
        while (Lines.Count < visible.Count)
        {
            var made = UnityEngine.Object.Instantiate(_window.ChatMessagePrefab.gameObject, _clone.transform)?.TryCast<GameObject>();
            if (made == null) break;
            var tmp = made.GetComponent<TextMeshProUGUI>();
            if (tmp == null)
            {
                UnityEngine.Object.Destroy(made);
                break;
            }

            tmp.richText = true;
            Lines.Add(new ChatLineSlot { Go = made, Text = tmp, BaseSize = tmp.fontSize });
        }

        int size = FontSize?.Value ?? 0;
        for (int i = 0; i < Lines.Count; i++)
        {
            var slot = Lines[i];
            if (i >= visible.Count)
            {
                if (slot.Go.activeSelf) slot.Go.SetActive(false);
                slot.Record = null;
                continue;
            }

            var r = visible[i];
            slot.Record = r;
            slot.Text.text = ChatFormat.Render(r, options, out slot.Spans);
            slot.Text.color = ColorOf(r.Kind, r.FromSelf);
            float wantSize = size > 0 ? size : slot.BaseSize;
            if (Math.Abs(slot.Text.fontSize - wantSize) > 0.01f) slot.Text.fontSize = wantSize;
            if (!slot.Go.activeSelf) slot.Go.SetActive(true);
            slot.Go.transform.SetSiblingIndex(i);
        }

        _drawnVersion = Log.Version;
        _drawnTab = _tab;
        _drawnOptions = optionsKey;
        if (atBottom && scroll != null)
        {
            Canvas.ForceUpdateCanvases();
            scroll.verticalNormalizedPosition = 0f;
        }
    }

    /// <summary>The game's own color for a kind as <c>#RRGGBB</c> (the Colors line draws an unchanged channel in it).</summary>
    internal static string GameHex(ChatKind kind)
    {
        var c = _window?.Colors;
        Color color = c == null ? Color.white : kind switch
        {
            ChatKind.Global or ChatKind.Region => c.GlobalText,
            ChatKind.Team => c.TeamText,
            ChatKind.WhisperFrom or ChatKind.WhisperTo => c.WhisperText,
            ChatKind.System or ChatKind.Lore => c.SystemText,
            ChatKind.Local => c.LocalText,
            _ => Color.white,
        };
        return "#" + ColorUtility.ToHtmlStringRGB(color);
    }

    /// <summary>The game's message color for a kind (its <c>ChatColorsAsset</c>, as <c>ChatMessageLine.Set</c> uses).</summary>
    private static Color ColorOf(ChatKind kind, bool self)
    {
        string? own = ChatColors.HexFor(Colors?.Value, kind);
        if (own != null && ColorUtility.TryParseHtmlString(own, out var mine)) return mine;
        var c = _window?.Colors;
        if (c == null) return Color.white;
        return kind switch
        {
            ChatKind.Global => c.GlobalText,
            ChatKind.Team => c.TeamText,
            ChatKind.WhisperFrom or ChatKind.WhisperTo => c.WhisperText,
            ChatKind.System => c.SystemText,
            ChatKind.Lore => c.LoreText,
            ChatKind.Local => c.LocalText,
            ChatKind.Session => new Color(0.6f, 0.6f, 0.6f, 1f),
            _ => Color.white,
        };
    }

    // ---- clicks --------------------------------------------------------------------------------------------

    private static void HandleClicks()
    {
        bool left = UnityInputRuntime.MouseButtonPressedThisFrame(0);
        bool right = UnityInputRuntime.MouseButtonPressedThisFrame(1);
        if (!left && !right) return;
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y)) return;
        var point = new Vector3(x, y, 0f);
        var viewport = _window?.ScrollView?.viewport;
        if (viewport == null || !RectTransformUtility.RectangleContainsScreenPoint(viewport, new Vector2(x, y), null)) return;
        if (ChatTabs.PointerOver(new Vector2(x, y))) return;
        foreach (var slot in Lines)
        {
            if (slot.Record == null || !slot.Go.activeInHierarchy) continue;
            var rect = slot.Go.transform.TryCast<RectTransform>();
            if (rect == null || !RectTransformUtility.RectangleContainsScreenPoint(rect, new Vector2(x, y), null)) continue;
            if (right)
            {
                LineMenu(slot.Record);
                return;
            }

            int link = TMP_TextUtilities.FindIntersectingLink(slot.Text, point, null);
            if (link < 0) return;
            string id = slot.Text.textInfo.linkInfo[link].GetLinkID();
            var span = ChatFormat.SpanForLink(id, slot.Spans);
            if (span.HasValue) OpenSpan(span.Value, slot.Record);
            return;
        }
    }

    /// <summary>CHAT7: right-click on a line: Copy, and for a player's line Whisper and Mute / Unmute.</summary>
    private static void LineMenu(ChatRecord r)
    {
        string user = r.User;
        if (r.Kind == ChatKind.Session || user.Length == 0 || r.FromSelf)
        {
            Copy(r);
            return;
        }

        bool muted = ChatMute.Contains(ChatMute.Parse(MutedPlayers?.Value), user);
        var options = new List<string> { "Copy line", "Whisper " + user, (muted ? "Unmute " : "Mute ") + user };
        UiDialogs.Choose(typeof(ChatView), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter, user, "", options, pick =>
        {
            if (pick == 0) Copy(r);
            else if (pick == 1) ChatWindow.InsertIntoInput("/w " + user + " ");
            else if (pick == 2 && MutedPlayers != null)
            {
                MutedPlayers.Value = ChatMute.Toggle(MutedPlayers.Value, user);
                Log.Add(new ChatRecord
                {
                    Kind = ChatKind.Session, TimeUtc = DateTime.UtcNow, Transient = true,
                    Text = (muted ? "Unmuted " : "Muted ") + user + (muted ? "" : " (right-click one of their lines to unmute)"),
                }, collapse: false);
            }
        }, "Cancel", null);
    }

    private static void Copy(ChatRecord r)
    {
        try
        {
            GUIUtility.systemCopyBuffer = ChatFormat.PlainText(r);
            Flash("Copied the line");
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: copy threw: " + e.Message); }
    }

    /// <summary>How long <see cref="Flash"/> shows its note.</summary>
    private const double FlashSeconds = 2.0;

    private static TMP_Text? _flashText;
    private static string _flashNote = "";
    private static string _flashRestore = "";
    private static DateTime _flashUntil;

    /// <summary>
    /// A short note in the input line's placeholder ("Copied the line"), put back after <see cref="FlashSeconds"/>
    /// unless the game changed the placeholder meanwhile (a channel switch). Never adds a line to the log.
    /// </summary>
    internal static void Flash(string note)
    {
        try
        {
            var text = _window?.ChatInputField?.placeholder?.TryCast<TMP_Text>();
            if (text == null) return;
            if (_flashText == null || _flashText.Pointer != text.Pointer || text.text != _flashNote) _flashRestore = text.text ?? "";
            _flashText = text;
            _flashNote = note;
            _flashUntil = DateTime.UtcNow.AddSeconds(FlashSeconds);
            text.text = note;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: flash threw: " + e.Message); }
    }

    /// <summary>Each frame: put the placeholder back once the note's time is up (and on detach).</summary>
    private static void EndFlash(bool now)
    {
        var text = _flashText;
        if (text == null || (!now && DateTime.UtcNow < _flashUntil)) return;
        _flashText = null;
        try
        {
            if (text.Pointer != IntPtr.Zero && text.text == _flashNote) text.text = _flashRestore;
        }
        catch { /* a gone placeholder needs nothing back */ }
    }

    /// <summary>The link dialog's line for a chat link: anyone in chat may have written it.</summary>
    internal const string LinkBody = "Someone shared this link in chat. It opens in your web browser; open only links you trust.";

    /// <summary>The location link confirm's button: it places the player's own map marker there.</summary>
    internal const string MarkerSetButton = "Set marker";

    /// <summary>The click as a mod's handler sees it.</summary>
    private static ChatLinkClick ClickOf(ChatSpan span, ChatRecord r)
    {
        var kind = span.Kind switch
        {
            ChatSpanKind.Mod => ChatLinkKind.Package,
            ChatSpanKind.Location => ChatLinkKind.Location,
            ChatSpanKind.Item => ChatLinkKind.Item,
            ChatSpanKind.ModLink => ChatLinkKind.Mod,
            _ => ChatLinkKind.Web,
        };
        var channel = (int)r.Kind is >= 0 and <= 8 ? (ChatChannel)(int)r.Kind : ChatChannel.Unknown;
        string shown = span.Display ?? span.Target;
        return new ChatLinkClick(kind, span.Target, shown, r.Source, r.User, r.FromSelf, channel, r.Text, span.Owner, span.LinkType, r.Restored);
    }

    private static void OpenSpan(ChatSpan span, ChatRecord record)
    {
        // Mods first (a mod link's owner, then every OnLinkClicked handler in load order); a handled click ends here.
        try
        {
            if (ChatLinkRegistry.Dispatch(ClickOf(span, record))) return;
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("Chat: link handlers threw: " + e.Message); }

        if (span.Kind == ChatSpanKind.ModLink) return;

        if (span.Kind == ChatSpanKind.Location)
        {
            if (!ChatTokens.TryParseLocation(span.Target, out int lx, out int lz)) return;
            bool have = ChatGameData.TryLocalPosition(out float px, out float pz);
            UiDialogs.Choose(typeof(ChatView), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter,
                ChatTokens.MarkerTitle(lx, lz), ChatTokens.MarkerPrompt(lx, lz, have, px, pz),
                new[] { MarkerSetButton },
                pick =>
                {
                    if (pick != 0) return;
                    try { WorldMap.SetMarker(lx, lz); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Chat: map marker threw: " + e.Message); }
                },
                Networking.JoinLinkText.CancelButton, null);
            return;
        }

        if (span.Kind == ChatSpanKind.Item)
        {
            var (title, body) = ItemContent(span.Target);
            UiDialogs.Notice(typeof(ChatView), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter, title, body, "OK", null);
            return;
        }

        if (span.Kind == ChatSpanKind.Mission)
        {
            int id = int.TryParse(span.Target, out int parsed) ? parsed : 0;
            string? name = ChatGameData.MissionName(id);
            UiDialogs.Notice(typeof(ChatView), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter, name ?? "Unknown hunt",
                name == null ? "Mission " + span.Target + " is not a servant hunt this game knows." : "A servant hunt zone.",
                "OK", null);
            return;
        }

        if (span.Kind == ChatSpanKind.Mod)
        {
            if (!ChatPolicy.Current.AllowModLinks) return;
            int at = span.Target.IndexOf('@');
            string package = at < 0 ? span.Target : span.Target.Substring(0, at);
            string? version = at < 0 ? null : span.Target.Substring(at + 1);
            string status = OpenModLink?.Invoke(package, version) ?? "mod links are not available";
            Plugin.Logger?.LogInfo("Chat: mod link " + span.Target + " -> " + status);
            return;
        }

        if (span.Kind != ChatSpanKind.Url || !ChatPolicy.Current.AllowLinks || !UiLinks.IsOpenable(span.Target)) return;
        string url = span.Target;
        UiDialogs.Choose(typeof(ChatView), SettingsPanel.PluginId, Progenitor.Ui.Ui.FrameworkFooter,
            Networking.JoinLinkText.Title, LinkBody + "\n\n" + url,
            new[] { Networking.JoinLinkText.OpenButton, Networking.JoinLinkText.CopyButton },
            pick =>
            {
                try
                {
                    if (pick == 0) Application.OpenURL(url);
                    else GUIUtility.systemCopyBuffer = url;
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("Chat: link action threw: " + e.Message); }
            },
            Networking.JoinLinkText.CancelButton, null);
    }

    /// <summary>An item link's name and description from the game (<see cref="ChatItemText"/>).</summary>
    private static (string Title, string Body) ItemContent(string target)
    {
        int id = int.TryParse(target, out int parsed) ? parsed : 0;
        string? name = id == 0 ? null : ChatGameData.ItemName(id);
        string? description = name == null ? null : ChatGameData.ItemDescription(id);
        return ChatItemText.Content(name, description);
    }

    // ---- item link hover: the item's name and description in Progenitor's tooltip box --------------------

    private static string _hoverKey = "";
    private static Func<bool>? _hoverCheck;
    private static bool _hoverWarned;
    private static float _hoverX = float.NaN, _hoverY = float.NaN;
    private static int _hoverDrawn = -1;
    private static float _hoverScroll = float.NaN;

    /// <summary>Whether the tooltip box is up with this view's item tooltip (another control may have taken it since).</summary>
    private static bool HoverOwned => _hoverCheck != null && UnityUiRuntime.TooltipShown && UnityUiRuntime.TooltipOwnedBy(_hoverCheck);

    /// <summary>
    /// While the pointer rests on an item link, show its tooltip; hide it when the pointer leaves the link. Looked at again
    /// when the pointer moves, when the lines are redrawn (a new line under a still pointer) and when the log scrolls.
    /// </summary>
    private static void HandleHover()
    {
        if (!UnityUiRuntime.TrySamplePointerScreenAnyFocus(out float x, out float y))
        {
            EndHover();
            return;
        }

        float scroll = _window?.ScrollView?.verticalNormalizedPosition ?? 0f;
        if (x == _hoverX && y == _hoverY && _drawnVersion == _hoverDrawn && scroll == _hoverScroll) return;
        _hoverX = x;
        _hoverY = y;
        _hoverDrawn = _drawnVersion;
        _hoverScroll = scroll;
        string key = "";
        string target = "";
        var point = new Vector3(x, y, 0f);
        if (!ChatTabs.PointerOver(new Vector2(x, y)))
        {
            foreach (var slot in Lines)
            {
                if (slot.Record == null || !slot.Go.activeInHierarchy) continue;
                var rect = slot.Go.transform.TryCast<RectTransform>();
                if (rect == null || !RectTransformUtility.RectangleContainsScreenPoint(rect, new Vector2(x, y), null)) continue;
                int link = TMP_TextUtilities.FindIntersectingLink(slot.Text, point, null);
                if (link >= 0)
                {
                    var span = ChatFormat.SpanForLink(slot.Text.textInfo.linkInfo[link].GetLinkID(), slot.Spans);
                    if (span.HasValue && span.Value.Kind == ChatSpanKind.Item)
                    {
                        target = span.Value.Target;
                        key = slot.Record.Seq.ToString(System.Globalization.CultureInfo.InvariantCulture) + ":" + link + ":" + target;
                    }
                }

                break;
            }
        }

        if (key.Length == 0)
        {
            HideHover();
            return;
        }

        if (key == _hoverKey && HoverOwned) return;
        HideHover();
        _hoverKey = key;
        try
        {
            if (WidgetRuntime.Current is not UnityUiRuntime runtime) return;
            var (title, body) = ItemContent(target);
            var anchor = ScreenRect.FromCorners(x - 12f, y - 12f, x + 12f, y + 12f);
            string shownFor = key;
            Func<bool> check = () => _hoverKey == shownFor;
            _hoverCheck = check;
            runtime.ShowGameControlTooltip(anchor, title, body, check);
            if (!HoverOwned) _hoverCheck = null;
        }
        catch (Exception e)
        {
            _hoverCheck = null;
            if (!_hoverWarned)
            {
                _hoverWarned = true;
                Plugin.Logger?.LogWarning("Chat: the item tooltip threw (logged once): " + e.Message);
            }
        }
    }

    /// <summary>The pointer left the log (or the view detached): forget it and hide the tooltip.</summary>
    private static void EndHover()
    {
        _hoverX = _hoverY = _hoverScroll = float.NaN;
        _hoverDrawn = -1;
        HideHover();
    }

    /// <summary>Hide the item tooltip only while the box still shows this view's tooltip, never another control's.</summary>
    private static void HideHover()
    {
        _hoverKey = "";
        bool owned = HoverOwned;
        _hoverCheck = null;
        if (!owned) return;
        try { WidgetRuntime.Current?.HideTooltip(); }
        catch { /* a gone box needs no hiding */ }
    }
}
#endif
