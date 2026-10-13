#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using BepInEx.Configuration;
using ProjectM.Network;
using Progenitor.Networking;
using Progenitor.Ui;
using Unity.Entities;

namespace Progenitor.Chat;

/// <summary>
/// CHAT4 (docs/chat.md, "Server settings (as built)"): the Server tab's Chat group at run time.
/// <list type="bullet">
/// <item><b>Both sides:</b> the synced rows (<c>progenitor.serverchat.cfg</c> <c>[ServerChat]</c>, ConfigSync rows of
/// <see cref="ConfigSync.FrameworkSettingsId"/>, admin-edited) and the <see cref="ChatPolicy"/> they fill. A client uses the
/// joined server's values while ConfigSync holds them, and <see cref="ChatPolicy.Defaults"/> otherwise (no Progenitor on
/// the server, before the values arrive, after leaving, after Core unloads).</item>
/// <item><b>Server:</b> the word filter and the Discord relay, from <c>[ChatFilter]</c> and <c>[ChatRelay]</c> of the server's
/// <c>progenitor.cfg</c> (edited over the admin channel; never synced). <see cref="Moderate"/> runs from Core's chat hook
/// after the <c>ChatMessage</c> subscribers; the relay posts from a background task, never on the game thread.</item>
/// </list>
/// </summary>
internal static class ServerChat
{
    private static readonly object Owner = new();
    private static readonly List<IDisposable> Registrations = new();
    private static bool _installed;
    private static ConfigFile? _file;
    private static ConfigEntry<bool>? _allowLinks;
    private static ConfigEntry<bool>? _allowModLinks;
    private static ConfigEntry<bool>? _allowAtAll;
    private static ConfigEntry<bool>? _allowLong;
    private static ConfigEntry<int>? _maxLong;

    // Server only: [ChatFilter] and [ChatRelay] of the shared progenitor.cfg.
    private static ConfigEntry<bool>? _filterOn;
    private static ConfigEntry<string>? _filterWords;
    private static ConfigEntry<string>? _filterAction;
    private static ConfigEntry<bool>? _relayOn;
    private static ConfigEntry<string>? _relayUrl;
    private static ConfigEntry<bool>? _relayGlobal;
    private static ConfigEntry<bool>? _relayLocal;
    private static ConfigEntry<bool>? _relayClan;
    private static ConfigEntry<bool>? _relayWhispers;
    private static ConfigEntry<bool>? _relaySystem;

    private static string _wordsRaw = "\u0000";
    private static List<string> _words = new();
    private static string _policyPrint = "";
    private static double _nextPolicyCheck;
    private static readonly System.Diagnostics.Stopwatch Clock = System.Diagnostics.Stopwatch.StartNew();

    private static readonly ChatRelayQueue Queue = new();
    private static readonly ChatLogThrottle FailureLog = new(60);
    private static readonly ChatLogThrottle DropLog = new(60);
    private static IChatRelaySender? _sender;
    private static CancellationTokenSource? _cancel;
    private static string _lastFailure = "";
    private static readonly object FailureGate = new();
    private static bool _relayWasUsable;

    private static double Now() => Clock.Elapsed.TotalSeconds;

    /// <summary>Bind the rows (before <see cref="JoinServerSettings.Install"/>, whose page places them). Idempotent.</summary>
    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        try
        {
            string dir;
            try { dir = BepInEx.Paths.ConfigPath; }
            catch { dir = ModManifest.ConfigDirectory; }
            _file = new ConfigFile(Path.Combine(dir, ServerChatOptions.FileName), saveOnInit: true);
            string s = ServerChatOptions.Section;
            _allowLinks = Config.ConfigHelper.Bind(_file, s, ServerChatOptions.AllowLinksKey, true,
                "Server. Web links in chat can be clicked on Progenitor clients (they always ask before opening). Sent to every player.");
            _allowModLinks = Config.ConfigHelper.Bind(_file, s, ServerChatOptions.AllowModLinksKey, true,
                "Server. Shared mod links open the mod's page in the Mods window. Sent to every player.");
            _allowAtAll = Config.ConfigHelper.Bind(_file, s, ServerChatOptions.AllowAtAllKey, true,
                "Server. An admin's @all counts as a mention for every player with Progenitor. Sent to every player.");
            _allowLong = Config.ConfigHelper.Bind(_file, s, ServerChatOptions.AllowLongMessagesKey, true,
                "Server. Players with Progenitor may send messages longer than the game's limit, in parts. Sent to every player.");
            _maxLong = Config.ConfigHelper.Bind(_file, s, ServerChatOptions.MaxLongMessageCharsKey, ChatPolicy.DefaultMaxLongMessageChars,
                "Server. The most characters a joined long message may have (" + ChatPolicy.MinLongMessageChars + " to "
                + ChatPolicy.MaxLongMessageCharsCap + "; a value outside is clamped). Sent to every player.");

            SettingsCatalog.Register(ConfigSync.FrameworkSettingsId, _file, Owner);
            foreach (var entry in SyncedEntries())
            {
                try { Registrations.Add(ConfigSync.Register(ConfigSync.FrameworkSettingsId, entry)); }
                catch (Exception e) { Plugin.Logger?.LogWarning("Chat: ConfigSync could not register " + entry.Definition + ": " + e.Message); }
            }

            if (Plugin.IsServerProcess)
            {
                Config.SharedConfig.BindWhenAvailable("[" + ServerChatOptions.FilterSection + "] and [" + ServerChatOptions.RelaySection + "]", BindShared);
                Chat.Broadcasted = OnBroadcast;
            }
            else
            {
                Handshake.ServerContextCleared -= OnServerLeft;
                Handshake.ServerContextCleared += OnServerLeft;
            }

            Plugin.Frame -= OnFrame;
            Plugin.Frame += OnFrame;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Chat: the server chat settings could not be bound: " + e.Message);
        }
    }

    internal static void Uninstall()
    {
        try { Plugin.Frame -= OnFrame; }
        catch { /* teardown */ }
        try { Handshake.ServerContextCleared -= OnServerLeft; }
        catch { /* teardown */ }
        Chat.Broadcasted = null;
        for (int i = Registrations.Count - 1; i >= 0; i--)
        {
            try { Registrations[i].Dispose(); }
            catch { /* teardown */ }
        }

        Registrations.Clear();
        StopRelay();
        ChatPolicy.Reset();
        _policyPrint = "";
        _nextPolicyCheck = 0;
        _allowLinks = _allowModLinks = _allowAtAll = _allowLong = null;
        _maxLong = null;
        _filterOn = _relayOn = _relayGlobal = _relayLocal = _relayClan = _relayWhispers = _relaySystem = null;
        _filterWords = _filterAction = _relayUrl = null;
        _wordsRaw = "\u0000";
        _words = new List<string>();
        _relayWasUsable = false;
        _file = null;
        _installed = false;
    }

    private static IEnumerable<ConfigEntryBase> SyncedEntries()
    {
        if (_allowLinks != null) yield return _allowLinks;
        if (_allowModLinks != null) yield return _allowModLinks;
        if (_allowAtAll != null) yield return _allowAtAll;
        if (_allowLong != null) yield return _allowLong;
        if (_maxLong != null) yield return _maxLong;
    }

    private static void BindShared(ConfigFile file)
    {
        if (!_installed) return;
        string f = ServerChatOptions.FilterSection, r = ServerChatOptions.RelaySection;
        _filterOn = Config.ConfigHelper.Bind(file, f, ServerChatOptions.EnabledKey, false,
            "Server. Check every player chat line for the filtered words (whole words, any case). Edited on the Server tab (Chat).");
        _filterWords = Config.ConfigHelper.Bind(file, f, ServerChatOptions.WordsKey, "",
            "Server. The filtered words or short phrases, separated by commas. Never sent to players.");
        _filterAction = Config.ConfigHelper.Bind(file, f, ServerChatOptions.ActionKey, ChatWordFilter.ActionReplace,
            "Server. replace (each filtered word becomes ***; the line is still sent) or block (the line is not sent; only its sender is told).");
        _relayOn = Config.ConfigHelper.Bind(file, r, ServerChatOptions.EnabledKey, false,
            "Server. Post the chosen chat channels to a Discord webhook (at most one post a second, a queue of 50 lines).");
        _relayUrl = Config.ConfigHelper.Bind(file, r, ServerChatOptions.WebhookUrlKey, "",
            "Server. The Discord webhook address (https://discord.com/api/webhooks/...). A secret: never sent to players or "
            + "shown in the game, never logged.");
        _relayGlobal = Config.ConfigHelper.Bind(file, r, ServerChatOptions.GlobalKey, true, "Server. Relay Global chat.");
        _relayLocal = Config.ConfigHelper.Bind(file, r, ServerChatOptions.LocalKey, false, "Server. Relay Local chat.");
        _relayClan = Config.ConfigHelper.Bind(file, r, ServerChatOptions.ClanKey, false, "Server. Relay clan chat (players expect it to be private).");
        _relayWhispers = Config.ConfigHelper.Bind(file, r, ServerChatOptions.WhispersKey, false, "Server. Relay whispers (players expect them to be private).");
        _relaySystem = Config.ConfigHelper.Bind(file, r, ServerChatOptions.SystemKey, true,
            "Server. Relay admins' System lines and server-wide notices from Progenitor and its mods.");
        Plugin.Logger?.LogInfo("Chat: word filter " + (Bool(_filterOn) ? "on" : "off") + ", Discord relay " + (Bool(_relayOn) ? "on" : "off")
            + " (webhook address " + (ChatRelayFormat.Redact(_relayUrl.Value).Length > 0 ? "set" : "not set") + ").");
    }

    private static bool Bool(ConfigEntry<bool>? e)
    {
        try { return e?.Value ?? false; }
        catch { return false; }
    }

    private static string Str(ConfigEntry<string>? e)
    {
        try { return e?.Value ?? ""; }
        catch { return ""; }
    }

    // ---- the page -----------------------------------------------------------------------------------

    /// <summary>The Chat group's synced rows (from <see cref="JoinServerSettings"/>'s page builder).</summary>
    internal static void AddRows(SettingsPageBuilder page)
    {
        if (_allowLinks == null || _allowModLinks == null || _allowAtAll == null || _allowLong == null || _maxLong == null) return;
        page.Toggle(_allowLinks, label: ServerChatOptions.AllowLinksLabel, help: ServerChatOptions.AllowLinksHelp, scope: SettingScope.Server);
        page.Toggle(_allowModLinks, label: ServerChatOptions.AllowModLinksLabel, help: ServerChatOptions.AllowModLinksHelp, scope: SettingScope.Server);
        page.Toggle(_allowAtAll, label: ServerChatOptions.AllowAtAllLabel, help: ServerChatOptions.AllowAtAllHelp, scope: SettingScope.Server);
        page.Toggle(_allowLong, label: ServerChatOptions.AllowLongMessagesLabel, help: ServerChatOptions.AllowLongMessagesHelp, scope: SettingScope.Server);
        page.Number(_maxLong, min: ChatPolicy.MinLongMessageChars, max: ChatPolicy.MaxLongMessageCharsCap, step: 100, units: "characters",
            label: ServerChatOptions.MaxLongMessageLabel, help: ServerChatOptions.MaxLongMessageHelp, scope: SettingScope.Server);
    }

    // ---- the policy ---------------------------------------------------------------------------------

    /// <summary>The policy from the rows as this side holds them now (a client: only while the server's values are held).</summary>
    internal static ChatPolicy CurrentPolicy()
    {
        bool fromServer;
        if (Plugin.IsServerProcess) fromServer = _allowLinks != null;
        else
        {
            try { fromServer = _allowLinks != null && ConfigSync.IsLocked(_allowLinks); }
            catch { fromServer = false; }
        }

        return ServerChatPolicy.From(fromServer, Bool(_allowLinks), Bool(_allowModLinks), Bool(_allowAtAll), Bool(_allowLong),
            SafeInt(_maxLong, ChatPolicy.DefaultMaxLongMessageChars));
    }

    private static int SafeInt(ConfigEntry<int>? e, int fallback)
    {
        try { return e?.Value ?? fallback; }
        catch { return fallback; }
    }

    private static void RefreshPolicy(string reason)
    {
        var policy = CurrentPolicy();
        string print = ServerChatPolicy.Fingerprint(policy) + (ReferenceEquals(policy, ChatPolicy.Defaults) ? "d" : "s");
        if (string.Equals(print, _policyPrint, StringComparison.Ordinal)) return;
        _policyPrint = print;
        ChatPolicy.Use(policy);
        Plugin.Logger?.LogInfo("Chat: policy " + (ReferenceEquals(policy, ChatPolicy.Defaults) ? "defaults" : "from the server")
            + " (" + reason + "): links " + OnOff(policy.AllowLinks) + ", mod links " + OnOff(policy.AllowModLinks) + ", @all "
            + OnOff(policy.AllowAtAll) + ", long messages " + OnOff(policy.AllowLongMessages) + " up to " + policy.MaxLongMessageChars + ".");
    }

    private static string OnOff(bool b) => b ? "on" : "off";

    private static void OnServerLeft(string reason)
    {
        _policyPrint = "";
        ChatPolicy.Reset();
        try { RefreshPolicy("left the server"); }
        catch { /* the next frame checks again */ }
    }

    private static void OnFrame()
    {
        double now = Now();
        if (now >= _nextPolicyCheck)
        {
            _nextPolicyCheck = now + 0.5;
            try { RefreshPolicy(Plugin.IsServerProcess ? "server settings" : "synced values"); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Chat: the policy check threw: " + e.Message); }
        }

        if (Plugin.IsServerProcess) RelayTick(now);
    }

    // ---- the filter and the relay (server) ----------------------------------------------------------

    /// <summary>True when the server's chat hook must run <see cref="Moderate"/> (a filter with words, or a usable relay).</summary>
    internal static bool Active => Plugin.IsServerProcess && (FilterWords().Count > 0 && Bool(_filterOn) || RelayUsable());

    private static List<string> FilterWords()
    {
        string raw = Str(_filterWords);
        if (!string.Equals(raw, _wordsRaw, StringComparison.Ordinal))
        {
            _wordsRaw = raw;
            _words = ChatWordFilter.Parse(raw);
        }

        return _words;
    }

    private static bool RelayUsable() => Bool(_relayOn) && ChatRelayFormat.UsableWebhookUrl(Str(_relayUrl));

    private static bool RelayChannelOn(ChatRelayChannel channel) => channel switch
    {
        ChatRelayChannel.Global => Bool(_relayGlobal),
        ChatRelayChannel.Local => Bool(_relayLocal),
        ChatRelayChannel.Clan => Bool(_relayClan),
        ChatRelayChannel.Whisper => Bool(_relayWhispers),
        ChatRelayChannel.System => Bool(_relaySystem),
        _ => false,
    };

    /// <summary>
    /// Server, main thread, from Core's chat hook for a player's line no subscriber cancelled. Returns whether to drop the
    /// line, and the text to send instead (null: unchanged). A blocked line's sender gets <see cref="ServerChatText.BlockedNotice"/>.
    /// A line that goes out is queued for the relay when its channel is on (command lines never are).
    /// </summary>
    internal static (bool Block, string? Replacement) Moderate(World world, Entity userEntity, string speakerName, string text, ChatMessageType type)
    {
        string send = text ?? "";
        string? replacement = null;
        if (Bool(_filterOn))
        {
            var words = FilterWords();
            if (words.Count > 0)
            {
                var outcome = ChatWordFilter.Decide(send, words, ChatWordFilter.ParseAction(Str(_filterAction)));
                if (outcome.Block)
                {
                    try { Chat.SendTo(world, userEntity, ServerChatText.BlockedNotice, ChatMessageType.System); }
                    catch (Exception e) { Plugin.Logger?.LogWarning("Chat: the blocked-line notice could not be sent: " + e.Message); }
                    return (true, null);
                }

                if (outcome.Matched)
                {
                    replacement = outcome.Text;
                    send = outcome.Text;
                }
            }
        }

        if (RelayUsable())
        {
            var channel = ChatRelayFormat.ChannelOf((int)type);
            string prefix = Progenitor.Commands.CommandRegistry.Prefix;
            bool command = prefix.Length > 0 && send.StartsWith(prefix, StringComparison.Ordinal);
            if (!command && channel != ChatRelayChannel.None && RelayChannelOn(channel))
                Enqueue(ChatRelayFormat.Line(speakerName, send, channel));
        }

        return (false, replacement);
    }

    /// <summary>Chat.Broadcast's notice: a server-wide System line with no speaker goes to the relay when System is on.</summary>
    private static void OnBroadcast(string text, ChatMessageType type, bool hasSpeaker)
    {
        if (hasSpeaker || type != ChatMessageType.System) return;
        if (!RelayUsable() || !Bool(_relaySystem)) return;
        Enqueue(ChatRelayFormat.Line(null, text, ChatRelayChannel.System));
    }

    private static void Enqueue(string line)
    {
        Queue.Enqueue(line);
    }

    private static void RelayTick(double now)
    {
        bool usable = RelayUsable();
        if (!usable)
        {
            if (_relayWasUsable) StopRelay();
            _relayWasUsable = false;
            return;
        }

        _relayWasUsable = true;
        int dropped = Queue.TakeDropped();
        if (dropped > 0 && DropLog.ShouldLog(now, out int heldDrops))
            Plugin.Logger?.LogWarning("Chat: Discord relay queue full; " + (dropped + heldDrops) + " older line(s) dropped.");
        WriteFailureLog(now);

        string? batch = Queue.TakeBatch(now);
        if (batch == null) return;
        _sender ??= new HttpChatRelaySender();
        _cancel ??= new CancellationTokenSource();
        var sender = _sender;
        var token = _cancel.Token;
        string url = Str(_relayUrl).Trim();
        string json = ChatRelayFormat.Json(batch);
        Task.Run(async () =>
        {
            ChatRelayResult result;
            try { result = await sender.PostAsync(url, json, token).ConfigureAwait(false); }
            catch (Exception e) { result = new ChatRelayResult(false, 0, 0, Scrub(e.GetType().Name + ": " + e.Message, url)); }
            if (token.IsCancellationRequested) return;
            Queue.Completed(Now(), result.Ok, result.RetryAfterSeconds);
            if (!result.Ok)
                lock (FailureGate)
                    _lastFailure = result.Status > 0 ? "HTTP " + result.Status + (result.Error.Length > 0 ? " (" + result.Error + ")" : "") : result.Error;
        });
    }

    private static void WriteFailureLog(double now)
    {
        string failure;
        lock (FailureGate)
        {
            failure = _lastFailure;
            if (failure.Length == 0) return;
            _lastFailure = "";
        }

        if (FailureLog.ShouldLog(now, out int held))
            Plugin.Logger?.LogWarning("Chat: Discord relay post failed: " + failure + (held > 0 ? " (" + held + " more since the last report)" : "") + ".");
    }

    /// <summary>The webhook address never reaches a log: any copy of it in an error text is replaced.</summary>
    internal static string Scrub(string text, string url)
    {
        string t = text ?? "";
        if (!string.IsNullOrEmpty(url)) t = t.Replace(url, "(webhook)");
        int at = t.IndexOf("/api/webhooks/", StringComparison.OrdinalIgnoreCase);
        return at >= 0 ? t.Substring(0, at) + "/api/webhooks/(hidden)" : t;
    }

    private static void StopRelay()
    {
        try { _cancel?.Cancel(); }
        catch { /* teardown */ }
        try { _cancel?.Dispose(); }
        catch { /* teardown */ }
        _cancel = null;
        try { _sender?.Dispose(); }
        catch { /* teardown */ }
        _sender = null;
        Queue.Clear();
        lock (FailureGate) _lastFailure = "";
    }
}

/// <summary>CHAT4: the live webhook sender over <see cref="HttpClient"/> (a 10 s timeout; no cookies, no redirects).</summary>
internal sealed class HttpChatRelaySender : IChatRelaySender
{
    private readonly HttpClient _client;

    internal HttpChatRelaySender()
    {
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false };
        _client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(10) };
        _client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "Progenitor-ChatRelay");
    }

    public async Task<ChatRelayResult> PostAsync(string url, string json, CancellationToken cancel)
    {
        using var content = new StringContent(json ?? "", Encoding.UTF8, "application/json");
        using var response = await _client.PostAsync(url, content, cancel).ConfigureAwait(false);
        int status = (int)response.StatusCode;
        if (status >= 200 && status < 300) return new ChatRelayResult(true, status, 0, "");
        double retry = 0;
        if (status == 429)
        {
            try
            {
                var after = response.Headers.RetryAfter;
                if (after?.Delta is TimeSpan delta) retry = delta.TotalSeconds;
            }
            catch { /* the default below */ }

            if (retry <= 0) retry = 2;
        }

        return new ChatRelayResult(false, status, retry, response.ReasonPhrase ?? "");
    }

    public void Dispose() => _client.Dispose();
}
#endif
