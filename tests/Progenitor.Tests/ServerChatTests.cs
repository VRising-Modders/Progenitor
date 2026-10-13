using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using Progenitor.Chat;
using Progenitor.Install;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT4 (docs/chat.md, "Server settings (as built)"): the server word filter. Game-free.</summary>
public sealed class ChatWordFilterTests
{
    [Fact]
    public void The_list_is_split_on_commas_semicolons_and_lines_trimmed_and_kept_once_ignoring_case()
    {
        Assert.Equal(new[] { "darn", "heck", "gosh darn" }, ChatWordFilter.Parse(" darn,heck;\nDARN\r\ngosh   darn, , ;"));
        Assert.Equal("darn, heck", ChatWordFilter.Normalize("darn\nheck"));
        Assert.Empty(ChatWordFilter.Parse(null));
        // Punctuation alone is not a word; an over-long word is left out.
        Assert.Empty(ChatWordFilter.Parse("***, !!, " + new string('a', ChatWordFilter.MaxWordChars + 1)));
        Assert.Equal(ChatWordFilter.MaxWords, ChatWordFilter.Parse(string.Join(",", Enumerable.Range(0, 300).Select(i => "w" + i))).Count);
    }

    [Fact]
    public void The_check_refuses_an_over_long_list_or_word()
    {
        Assert.Null(ChatWordFilter.Check("darn, heck"));
        Assert.NotNull(ChatWordFilter.Check(new string('a', ChatWordFilter.MaxListChars + 1)));
        Assert.NotNull(ChatWordFilter.Check("ok, " + new string('b', ChatWordFilter.MaxWordChars + 1)));
    }

    [Theory]
    [InlineData("you darn fool", true)]
    [InlineData("DARN it", true)]
    [InlineData("darn!", true)]
    [InlineData("(darn)", true)]
    [InlineData("darned", false)]
    [InlineData("undarn", false)]
    [InlineData("darn_it", false)]
    [InlineData("darn2", false)]
    [InlineData("", false)]
    public void Matching_is_whole_words_and_ignores_case(string text, bool matches) =>
        Assert.Equal(matches, ChatWordFilter.Matches(text, new[] { "darn" }));

    [Fact]
    public void A_short_word_does_not_match_inside_a_longer_one()
    {
        Assert.False(ChatWordFilter.Matches("a class act", new[] { "ass" }));
        Assert.True(ChatWordFilter.Matches("what an ass.", new[] { "ass" }));
    }

    [Fact]
    public void Replace_masks_every_whole_match_and_phrases_first()
    {
        var words = new[] { "darn", "gosh darn" };
        Assert.Equal("*** it, *** and darned ***", ChatWordFilter.Mask("Gosh darn it, darn and darned DARN", words));
        Assert.Equal("nothing here", ChatWordFilter.Mask("nothing here", words));
    }

    [Fact]
    public void Decide_passes_replaces_or_blocks()
    {
        var words = new[] { "darn" };
        var pass = ChatWordFilter.Decide("hello", words, ChatFilterAction.Block);
        Assert.False(pass.Matched);
        Assert.False(pass.Block);
        Assert.Equal("hello", pass.Text);

        var replaced = ChatWordFilter.Decide("darn it", words, ChatFilterAction.Replace);
        Assert.True(replaced.Matched);
        Assert.False(replaced.Block);
        Assert.Equal("*** it", replaced.Text);

        var blocked = ChatWordFilter.Decide("darn it", words, ChatFilterAction.Block);
        Assert.True(blocked.Block);
        Assert.Equal("darn it", blocked.Text);

        Assert.Equal(ChatFilterAction.Block, ChatWordFilter.ParseAction(" Block "));
        Assert.Equal(ChatFilterAction.Replace, ChatWordFilter.ParseAction("anything else"));
    }
}

/// <summary>CHAT4: the Discord relay's formatting, escaping, address check, queue and log throttle. Game-free.</summary>
public sealed class ChatRelayTests
{
    [Fact]
    public void Channels_follow_the_games_chat_types_and_Region_is_never_relayed()
    {
        Assert.Equal(ChatRelayChannel.Global, ChatRelayFormat.ChannelOf(0));
        Assert.Equal(ChatRelayChannel.None, ChatRelayFormat.ChannelOf(1));
        Assert.Equal(ChatRelayChannel.Clan, ChatRelayFormat.ChannelOf(2));
        Assert.Equal(ChatRelayChannel.Whisper, ChatRelayFormat.ChannelOf(3));
        Assert.Equal(ChatRelayChannel.Local, ChatRelayFormat.ChannelOf(4));
        Assert.Equal(ChatRelayChannel.System, ChatRelayFormat.ChannelOf(5));
        Assert.Equal(ChatRelayChannel.None, ChatRelayFormat.ChannelOf(99));
    }

    [Fact]
    public void A_line_is_name_colon_message_with_the_channel_named_off_Global()
    {
        Assert.Equal("Mira: hello", ChatRelayFormat.Line("Mira", "hello", ChatRelayChannel.Global));
        Assert.Equal("[Local] Mira: hi", ChatRelayFormat.Line("Mira", " hi ", ChatRelayChannel.Local));
        Assert.Equal("[System] Server restarts in 5 minutes", ChatRelayFormat.Line(null, "Server restarts in 5 minutes", ChatRelayChannel.System));
        Assert.Equal("Mira: two lines", ChatRelayFormat.Line("Mira", "two\r\nlines", ChatRelayChannel.Global));
    }

    [Fact]
    public void Every_at_sign_gets_a_zero_width_space_so_nothing_pings()
    {
        string z = ChatRelayFormat.ZeroWidthSpace.ToString();
        Assert.Equal("@" + z + "everyone and @" + z + "here", ChatRelayFormat.EscapeMentions("@everyone and @here"));
        Assert.Equal("<@" + z + "123> <@" + z + "&456>", ChatRelayFormat.EscapeMentions("<@123> <@&456>"));
        Assert.Equal("@" + z + "x", ChatRelayFormat.EscapeMentions("@" + z + "x"));
        Assert.Equal("no mention", ChatRelayFormat.EscapeMentions("no mention"));
    }

    [Fact]
    public void Content_is_cut_to_2000_characters_without_splitting_a_pair()
    {
        Assert.Equal(2000, ChatRelayFormat.Content(new string('a', 5000)).Length);
        Assert.EndsWith("…", ChatRelayFormat.Content(new string('a', 5000)));
        string pairs = string.Concat(Enumerable.Repeat("\U0001F600", 1500));
        string cut = ChatRelayFormat.Content(pairs);
        Assert.True(cut.Length <= 2000);
        Assert.False(char.IsHighSurrogate(cut[cut.Length - 2]));
        Assert.Equal("short", ChatRelayFormat.Content("short"));
    }

    [Fact]
    public void The_body_is_json_content_with_no_mentions_allowed()
    {
        using var doc = JsonDocument.Parse(ChatRelayFormat.Json("Mira: \"hi\"\n<b>"));
        Assert.Equal("Mira: \"hi\"\n<b>", doc.RootElement.GetProperty("content").GetString());
        Assert.Equal(0, doc.RootElement.GetProperty("allowed_mentions").GetProperty("parse").GetArrayLength());
    }

    [Theory]
    [InlineData("", true)]
    [InlineData("https://discord.com/api/webhooks/123/abc", true)]
    [InlineData("https://discordapp.com/api/webhooks/123/abc", true)]
    [InlineData("https://ptb.discord.com/api/webhooks/123/abc", true)]
    [InlineData("http://discord.com/api/webhooks/123/abc", false)]
    [InlineData("https://discord.com.evil.example/api/webhooks/123/abc", false)]
    [InlineData("https://evil.example/api/webhooks/123/abc", false)]
    [InlineData("https://discord.com/channels/1/2", false)]
    [InlineData("https://discord.com/api/webhooks/", false)]
    [InlineData("https://user:pw@discord.com/api/webhooks/1/a", false)]
    [InlineData("https://discord.com:8443/api/webhooks/1/a", false)]
    [InlineData("not a url", false)]
    public void Only_Discord_webhook_addresses_are_accepted(string url, bool ok) =>
        Assert.Equal(ok, ChatRelayFormat.CheckWebhookUrl(url) == null);

    [Fact]
    public void The_address_is_redacted_to_set_or_empty()
    {
        Assert.Equal("set", ChatRelayFormat.Redact("https://discord.com/api/webhooks/123/secret"));
        Assert.Equal("", ChatRelayFormat.Redact("  "));
        Assert.True(ChatRelayFormat.UsableWebhookUrl("https://discord.com/api/webhooks/1/a"));
        Assert.False(ChatRelayFormat.UsableWebhookUrl(""));
    }

    [Fact]
    public void One_request_at_a_time_at_most_once_a_second()
    {
        var q = new ChatRelayQueue();
        Assert.Null(q.TakeBatch(0));
        q.Enqueue("a");
        Assert.Equal("a", q.TakeBatch(0));
        q.Enqueue("b");
        Assert.Null(q.TakeBatch(5)); // still in flight
        q.Completed(0.2, ok: true);
        Assert.Null(q.TakeBatch(0.5)); // within the second
        Assert.Equal("b", q.TakeBatch(1.0));
    }

    [Fact]
    public void Waiting_lines_go_out_together_up_to_one_message()
    {
        var q = new ChatRelayQueue();
        for (int i = 0; i < 3; i++) q.Enqueue("line " + i);
        Assert.Equal("line 0\nline 1\nline 2", q.TakeBatch(0));
        q.Completed(0, ok: true);
        q.Enqueue(new string('x', 1500));
        q.Enqueue(new string('y', 1500));
        Assert.Equal(new string('x', 1500), q.TakeBatch(1));
        q.Completed(1, ok: true);
        Assert.Equal(new string('y', 1500), q.TakeBatch(2));
    }

    [Fact]
    public void The_queue_keeps_50_lines_and_drops_the_oldest()
    {
        var q = new ChatRelayQueue();
        for (int i = 0; i < 60; i++) q.Enqueue("l" + i);
        Assert.Equal(ChatRelayQueue.Capacity, q.Count);
        Assert.Equal(10, q.TakeDropped());
        Assert.Equal(0, q.TakeDropped());
        string batch = q.TakeBatch(0)!;
        Assert.StartsWith("l10\n", batch);
    }

    [Fact]
    public void A_rate_limited_post_is_retried_after_the_wait_and_other_failures_are_dropped()
    {
        var q = new ChatRelayQueue();
        q.Enqueue("a");
        Assert.Equal("a", q.TakeBatch(0));
        q.Completed(0.1, ok: false, retryAfterSeconds: 5);
        Assert.Equal(1, q.Count);
        Assert.Null(q.TakeBatch(3));
        Assert.Equal("a", q.TakeBatch(5.2));
        q.Completed(5.3, ok: false);
        Assert.Equal(0, q.Count);
        Assert.False(q.InFlight);
    }

    [Fact]
    public void Lines_are_escaped_and_cut_as_they_are_queued()
    {
        var q = new ChatRelayQueue();
        q.Enqueue("@everyone " + new string('z', 3000));
        string batch = q.TakeBatch(0)!;
        Assert.Equal(ChatRelayFormat.MaxContentChars, batch.Length);
        Assert.StartsWith("@​everyone", batch);
    }

    [Fact]
    public void Failures_are_logged_once_a_minute_with_the_count_held_back()
    {
        var t = new ChatLogThrottle(60);
        Assert.True(t.ShouldLog(0, out int held));
        Assert.Equal(0, held);
        Assert.False(t.ShouldLog(10, out _));
        Assert.False(t.ShouldLog(30, out _));
        Assert.True(t.ShouldLog(61, out held));
        Assert.Equal(2, held);
    }
}

/// <summary>CHAT4: the server's synced switches mapped to the client policy. Game-free.</summary>
public sealed class ServerChatPolicyTests
{
    [Fact]
    public void Without_the_servers_values_the_defaults_apply()
    {
        Assert.Same(ChatPolicy.Defaults, ServerChatPolicy.From(false, false, false, false, false, 200));
    }

    [Fact]
    public void The_servers_values_fill_every_field_and_the_length_is_clamped()
    {
        var p = ServerChatPolicy.From(true, allowLinks: false, allowModLinks: true, allowAtAll: false, allowLongMessages: true, maxLongMessageChars: 9000);
        Assert.False(p.AllowLinks);
        Assert.True(p.AllowModLinks);
        Assert.False(p.AllowAtAll);
        Assert.True(p.AllowLongMessages);
        Assert.Equal(ChatPolicy.MaxLongMessageCharsCap, p.MaxLongMessageChars);
        Assert.Equal(ChatPolicy.MinLongMessageChars, ServerChatPolicy.From(true, true, true, true, true, 5).MaxLongMessageChars);
        Assert.Equal(1500, ServerChatPolicy.From(true, true, true, true, true, 1500).MaxLongMessageChars);
    }

    [Fact]
    public void The_fingerprint_changes_with_any_field()
    {
        var a = ServerChatPolicy.Fingerprint(ServerChatPolicy.From(true, true, true, true, true, 1000));
        Assert.Equal(a, ServerChatPolicy.Fingerprint(ChatPolicy.Defaults));
        Assert.NotEqual(a, ServerChatPolicy.Fingerprint(ServerChatPolicy.From(true, false, true, true, true, 1000)));
        Assert.NotEqual(a, ServerChatPolicy.Fingerprint(ServerChatPolicy.From(true, true, true, true, true, 1100)));
    }

    [Fact]
    public void The_synced_rows_and_the_server_only_rows_are_placed_in_the_Chat_group()
    {
        foreach (string key in ServerChatOptions.SyncedKeys)
        {
            var place = SettingPlaces.Find(ServerChatOptions.RowId(key))!;
            Assert.Equal(SettingPlaces.ChatServer, place.Home);
            Assert.Equal(SettingPlaces.StoreCfg, place.Store);
            Assert.Contains("progenitor.serverchat.cfg [ServerChat] " + key, place.Source);
        }

        foreach (string op in ServerChatOptions.AdminOps)
        {
            var place = SettingPlaces.Find(SettingPlaces.JoinAdminId(op))!;
            Assert.Equal(SettingPlaces.ChatServer, place.Home);
            Assert.Equal(SettingPlaces.StoreJoinAdmin, place.Store);
            Assert.Contains(ServerSettingsLock.GroupChat, ServerSettingsLock.GroupsOf(SettingPlaces.JoinAdminId(op)));
        }

        Assert.Contains(ServerSettingsLock.GroupChat, ServerSettingsLock.GroupsOf(ServerChatOptions.RowId(ServerChatOptions.AllowLinksKey)));
        Assert.True(ServerSettingsLock.IsGroup("chat"));
        // The player's own chat settings ([Chat] of progenitor.ui.cfg) stay on Progenitor's page.
        Assert.Equal(SettingPlaces.Chat, SettingPlaces.Find("progenitor/Chat/VisibleSeconds")!.Home);
        // The webhook address's cfg entry is never drawn and never in the readback.
        var url = ServerOptions.Find(ServerOptionOps.ChatRelayUrl)!;
        Assert.Equal(SettingPlaces.CfgOnly, SettingPlaces.Find(SettingPlaces.Id(url.Section, url.Key))!.Home);
        var readback = SettingPlaces.Readback(_ => ("https://discord.com/api/webhooks/1/secret", "x"));
        Assert.False(readback.ContainsKey(SettingPlaces.Id(url.Section, url.Key)));
    }

    [Fact]
    public void The_admin_rows_read_the_servers_words_and_never_show_the_address()
    {
        Assert.Equal("Not set", ServerChatRows.SecretDisplay(""));
        Assert.Equal("Set", ServerChatRows.SecretDisplay("set"));
        Assert.Equal("None", ServerChatRows.WordsDisplay(" "));
        var url = ServerChatRows.RelayUrl();
        Assert.True(url.WriteOnly);
        Assert.Equal(AdminSettingKind.Text, url.Kind);
        var action = ServerChatRows.FilterAction();
        Assert.Equal(new[] { "replace", "block" }, action.Choices);
        Assert.Equal("Replace with ***", action.DisplayOf("replace"));
        Assert.Equal(ServerChatOptions.AdminOps, ServerChatRows.All().Select(s => s.Key).ToArray());
        // Defaults: Global and System are relayed; clan chat and whispers only when an admin turns them on.
        Assert.Equal("true", ServerChatOptions.DefaultOf(ServerOptionOps.ChatRelayGlobal));
        Assert.Equal("true", ServerChatOptions.DefaultOf(ServerOptionOps.ChatRelaySystem));
        Assert.Equal("false", ServerChatOptions.DefaultOf(ServerOptionOps.ChatRelayClan));
        Assert.Equal("false", ServerChatOptions.DefaultOf(ServerOptionOps.ChatRelayWhispers));
        Assert.Equal("false", ServerChatOptions.DefaultOf(ServerOptionOps.ChatRelayLocal));
    }
}

/// <summary>CHAT4: the Chat group's server-only values over the admin channel: kinds, normalizing, and the secret address. Game-free.</summary>
[Collection("ServerSettingsLock")]
public sealed class ServerChatOptionsTests : IDisposable
{
    private static readonly DateTime T0 = new(2026, 10, 7, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RemoteInstallActor Admin = new("Admin", 76561198000000001UL, isAdmin: true);
    private const string Url = "https://discord.com/api/webhooks/123/topsecrettoken";

    private readonly string _dir;
    private readonly List<string> _audit = new();
    private readonly Dictionary<string, string> _values = new(StringComparer.Ordinal);
    private readonly List<(string Op, string Value)> _stored = new();
    private double _now = 1000;

    public ServerChatOptionsTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-serverchat-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        ModManifest.TestLoadedMods = Array.Empty<LoadedModInfo>();
        ModManifest.TestIsServer = true;
        Plugin.Logger = new TestLogSource();
        JoinAdminServer.Reset();
        JoinAdminServer.WindowGet = () => 60;
        JoinAdminServer.WindowSet = _ => true;
        JoinAdminServer.Audit = _audit.Add;
        JoinAdminServer.AuditClock = () => T0;
        JoinAdminServer.Now = () => _now;
        RefusedJoins.Clock = () => T0;
        foreach (string op in ServerChatOptions.AdminOps) _values[op] = ServerChatOptions.DefaultOf(op);
        ServerOptions.Get = op => _values.TryGetValue(op, out var v) ? v : null;
        ServerOptions.Set = (op, v) =>
        {
            _stored.Add((op, v));
            _values[op] = v;
            return true;
        };
        ServerSettingsLock.Reset();
        ServerSettingsLock.Source = () => "";
        ServerSettingsLock.LegacyAllowedIdsLocked = () => false;
    }

    public void Dispose()
    {
        JoinAdminServer.Uninstall();
        ServerSettingsLock.Reset();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        ModManifest.TestLoadedMods = null;
        ModManifest.TestIsServer = false;
        Plugin.Logger = null;
        try { Directory.Delete(_dir, true); } catch { /* temp */ }
    }

    private JoinAdminResponse Apply(JoinAdminRequest r)
    {
        _now += 7;
        return JoinAdminServer.Apply(r, handshakeOk: true, Admin);
    }

    private static JoinAdminRequest Value(string op, string value) => new() { Op = op, Value = value };

    [Fact]
    public void The_word_list_is_stored_normalized_and_may_be_cleared_over_the_wire()
    {
        var r = Apply(Value(ServerOptionOps.ChatFilterWords, "darn\nheck; DARN"));
        Assert.True(r.Ok, r.Error);
        Assert.Equal("darn, heck", _values[ServerOptionOps.ChatFilterWords]);
        Assert.Equal("darn, heck", r.State!.Options[ServerOptionOps.ChatFilterWords]);

        var bytes = JoinAdminWire.EncodeRequest(Value(ServerOptionOps.ChatFilterWords, ""));
        Assert.True(JoinAdminWire.TryDecodeRequest(bytes, out var decoded));
        Assert.True(decoded.HasValue);
        Assert.True(Apply(decoded).Ok);
        Assert.Equal("", _values[ServerOptionOps.ChatFilterWords]);

        var tooLong = Apply(Value(ServerOptionOps.ChatFilterWords, new string('a', ChatWordFilter.MaxListChars + 1)));
        Assert.False(tooLong.Ok);
    }

    [Fact]
    public void The_action_is_one_of_its_words()
    {
        Assert.True(Apply(Value(ServerOptionOps.ChatFilterAction, "Block")).Ok);
        Assert.Equal("block", _values[ServerOptionOps.ChatFilterAction]);
        var bad = Apply(Value(ServerOptionOps.ChatFilterAction, "shout"));
        Assert.False(bad.Ok);
        Assert.Equal("block", _values[ServerOptionOps.ChatFilterAction]);
    }

    [Fact]
    public void The_webhook_address_is_stored_but_never_sent_or_audited()
    {
        var r = Apply(Value(ServerOptionOps.ChatRelayUrl, " " + Url + " "));
        Assert.True(r.Ok, r.Error);
        Assert.Equal(Url, _values[ServerOptionOps.ChatRelayUrl]);
        Assert.Equal("set", r.State!.Options[ServerOptionOps.ChatRelayUrl]);
        Assert.DoesNotContain(_audit, line => line.Contains("topsecrettoken", StringComparison.Ordinal) || line.Contains("/api/webhooks", StringComparison.Ordinal));
        string wire = System.Text.Encoding.UTF8.GetString(JoinAdminWire.EncodeResponse(r));
        Assert.DoesNotContain("topsecrettoken", wire);

        // Storing the same address again is still a write (the shown value cannot tell two addresses apart).
        Assert.True(Apply(Value(ServerOptionOps.ChatRelayUrl, Url)).Ok);
        Assert.Equal(2, _stored.Count(s => s.Op == ServerOptionOps.ChatRelayUrl));

        // A refused address is audited without its text.
        var bad = Apply(Value(ServerOptionOps.ChatRelayUrl, "https://evil.example/api/webhooks/1/othersecret"));
        Assert.False(bad.Ok);
        Assert.DoesNotContain(_audit, line => line.Contains("othersecret", StringComparison.Ordinal));
        Assert.Equal(Url, _values[ServerOptionOps.ChatRelayUrl]);

        // Empty clears it.
        var cleared = Apply(Value(ServerOptionOps.ChatRelayUrl, ""));
        Assert.True(cleared.Ok, cleared.Error);
        Assert.Equal("", _values[ServerOptionOps.ChatRelayUrl]);
        Assert.Equal("", cleared.State!.Options[ServerOptionOps.ChatRelayUrl]);
    }

    [Fact]
    public void A_locked_chat_group_refuses_every_chat_edit()
    {
        ServerSettingsLock.Source = () => "chat";
        var r = Apply(Value(ServerOptionOps.ChatRelay, "true"));
        Assert.False(r.Ok);
        Assert.Contains("Locked", r.Error);
        Assert.Empty(_stored);
    }
}
