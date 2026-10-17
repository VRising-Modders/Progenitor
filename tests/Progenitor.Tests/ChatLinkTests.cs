using System;
using System.Collections.Generic;
using Progenitor.Chat;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Mod link tokens, line provenance, link handler order and the item link text.</summary>
public sealed class ChatLinkTests : IDisposable
{
    public ChatLinkTests() => ChatLinkRegistry.Clear();

    public void Dispose() => ChatLinkRegistry.Clear();

    private static readonly object OwnerA = new();
    private static readonly object OwnerB = new();

    // ---- token ------------------------------------------------------------------------------------------

    [Fact]
    public void Format_escapes_and_parse_round_trips()
    {
        string token = ChatLinks.ModLink("Satisvampory", "Chest", @"a|b]c\d:e", "Storage | (Plot 1]) \\");
        Assert.Equal(@"[mod:satisvampory:chest:a\|b\]c\\d:e|Storage \| (Plot 1\]) \\]", token);
        Assert.True(ChatLinkToken.TryParse(token, out var t));
        Assert.Equal("satisvampory", t.Owner);
        Assert.Equal("chest", t.Type);
        Assert.Equal(@"a|b]c\d:e", t.Payload);
        Assert.Equal(@"Storage | (Plot 1]) \", t.Text);
        Assert.Equal(token.Length, t.Length);
    }

    [Fact]
    public void Format_cleans_text_and_refuses_bad_parts()
    {
        Assert.Equal("[mod:m:k:1|‹b›x y]", ChatLinks.ModLink("m", "k", "1", "<b>x\ny"));
        Assert.Equal("[mod:m:k:42]", ChatLinks.ModLink("m", "k", "42", ""));
        Assert.Throws<ArgumentException>(() => ChatLinks.ModLink("has space", "k", "1", "x"));
        Assert.Throws<ArgumentException>(() => ChatLinks.ModLink("m", "k:x", "1", "x"));
        Assert.Throws<ArgumentException>(() => ChatLinks.ModLink("m", "k", "<color>", "x"));
        Assert.Throws<ArgumentException>(() => ChatLinks.ModLink("m", "k", new string('p', ChatLinkToken.MaxPayload + 1), "x"));
        Assert.Throws<ArgumentException>(() => ChatLinks.ModLink("m", "k", "", ""));
        string longText = ChatLinks.ModLink("m", "k", "1", new string('t', 200));
        Assert.True(ChatLinkToken.TryParse(longText, out var t));
        Assert.Equal(ChatLinkToken.MaxText, t.Text.Length);
        Assert.EndsWith("…", t.Text);
    }

    [Fact]
    public void Find_is_strict_and_disjoint_from_package_tokens()
    {
        string line = "5,000 [item:862477668] → [mod:satisvampory:chest:7f3a|Storage (Plot 1)] and [mod:Ns-Name@1.2] "
            + "[mod:x:y:z|open] [mod:x:y:bad\\q] [mod:x:y:<b>] [mod:x::z] [mod::y:z] [mod:x:y:z|] [mod:x:y:z";
        var found = ChatLinkToken.Find(line);
        Assert.Equal(2, found.Count);
        Assert.Equal("satisvampory", found[0].Owner);
        Assert.Equal("7f3a", found[0].Payload);
        Assert.Equal("Storage (Plot 1)", found[0].Text);
        Assert.Equal("open", found[1].Text);
        Assert.False(ModLinkToken.TryParse("[mod:satisvampory:chest:7f3a|Storage]", out _, out _));
        Assert.Empty(ChatLinkToken.Find("[mod:Ns-Name@1.2]"));
    }

    [Fact]
    public void Defuse_turns_every_link_token_into_plain_text()
    {
        Assert.Equal("(mod:a:b:c|x] (item:5] (loc:1,2] [other] ", ChatLinks.Defuse("[mod:a:b:c|x] [item:5] [loc:1,2] [other] "));
        Assert.Empty(ChatLinkToken.Find(ChatLinks.Defuse("[mod:a:b:c|x]")));
        Assert.Equal("", ChatLinks.Defuse(null));
    }

    // ---- provenance --------------------------------------------------------------------------------------

    [Theory]
    [InlineData((int)ChatKind.System, false, ChatLinkSource.Server)]
    [InlineData((int)ChatKind.Lore, false, ChatLinkSource.Server)]
    [InlineData((int)ChatKind.System, true, ChatLinkSource.Player)] // an admin typing on the System channel
    [InlineData((int)ChatKind.Global, false, ChatLinkSource.Player)]
    [InlineData((int)ChatKind.Global, true, ChatLinkSource.Player)]
    [InlineData((int)ChatKind.WhisperFrom, true, ChatLinkSource.Player)]
    [InlineData((int)ChatKind.Local, true, ChatLinkSource.Player)]
    public void Provenance_comes_from_the_channel_and_the_sender_id(int kind, bool hasSender, ChatLinkSource expected) =>
        Assert.Equal(expected, ChatRecord.SourceOf((ChatKind)kind, hasSender));

    [Fact]
    public void Same_text_renders_by_provenance_not_by_content()
    {
        const string text = "go [mod:satisvampory:chest:7f3a|Storage (Plot 1)] now";
        var options = new ChatFormat.Options();
        var server = new ChatRecord { Kind = ChatKind.System, Text = text, Source = ChatLinkSource.Server };
        var player = new ChatRecord { Kind = ChatKind.Global, User = "Mallory", Text = text, Source = ChatLinkSource.Player };

        // No handler: the server's line shows the text only, the player's stays as written.
        string s = ChatFormat.Render(server, options, out var sSpans);
        Assert.Contains("Storage (Plot 1)", s);
        Assert.DoesNotContain("[mod:", s);
        Assert.DoesNotContain("<link=", s);
        Assert.Single(sSpans);
        Assert.False(sSpans[0].IsLink);
        string p = ChatFormat.Render(player, options, out var pSpans);
        Assert.Contains("[mod:satisvampory:chest:7f3a|Storage (Plot 1)]", p);
        Assert.Empty(pSpans);

        // A handler: the server's line is a link, the player's still as written.
        using var h = ChatLinkRegistry.OnMod(OwnerA, "a", "satisvampory", _ => true, acceptPlayerLinks: false);
        s = ChatFormat.Render(server, options, out sSpans);
        Assert.Contains("<link=\"n:0\">", s);
        Assert.Equal(ChatSpanKind.ModLink, sSpans[0].Kind);
        Assert.Equal("7f3a", sSpans[0].Target);
        Assert.Equal("chest", sSpans[0].LinkType);
        Assert.NotNull(ChatFormat.SpanForLink("n:0", sSpans));
        ChatFormat.Render(player, options, out pSpans);
        Assert.Empty(pSpans);
    }

    [Fact]
    public void Player_links_are_clickable_only_when_the_owner_accepts_them()
    {
        var player = new ChatRecord { Kind = ChatKind.Global, User = "Bob", Text = "[mod:m:k:1|x]" };
        using var h = ChatLinkRegistry.OnMod(OwnerA, "a", "m", _ => true, acceptPlayerLinks: true);
        ChatFormat.Render(player, new ChatFormat.Options(), out var spans);
        Assert.Single(spans);
        Assert.Equal(ChatSpanKind.ModLink, spans[0].Kind);
    }

    [Fact]
    public void Provenance_is_saved_with_the_history_and_old_lines_are_players()
    {
        var log = new ChatLog();
        log.Add(new ChatRecord { Kind = ChatKind.System, Text = "a", Source = ChatLinkSource.Server, TimeUtc = DateTime.UtcNow }, false);
        log.Add(new ChatRecord { Kind = ChatKind.System, User = "Admin", Text = "a", TimeUtc = DateTime.UtcNow }, true);
        var back = ChatLog.Deserialize(log.Serialize(100), 100);
        Assert.Equal(2, back.Count); // the admin's line did not collapse into the server's
        Assert.Equal(ChatLinkSource.Server, back[0].Source);
        Assert.Equal(ChatLinkSource.Player, back[1].Source);
        var legacy = ChatLog.Deserialize("{\"t\":\"2026-10-01T00:00:00Z\",\"k\":6,\"u\":\"\",\"x\":\"a\"}", 10);
        Assert.Equal(ChatLinkSource.Player, legacy[0].Source);
    }

    // ---- handlers ----------------------------------------------------------------------------------------

    private static ChatLinkClick Click(ChatLinkKind kind, string owner = "", ChatLinkSource source = ChatLinkSource.Server) =>
        new(kind, "1", "x", source, "", false, ChatChannel.System, "line", owner, owner.Length > 0 ? "k" : "");

    [Fact]
    public void Owner_runs_first_then_general_handlers_in_order_until_one_handles()
    {
        var calls = new List<string>();
        using var g1 = ChatLinkRegistry.OnClick(OwnerA, "a", c => { calls.Add("g1"); return false; });
        using var g2 = ChatLinkRegistry.OnClick(OwnerB, "b", c => { calls.Add("g2"); return true; });
        using var g3 = ChatLinkRegistry.OnClick(OwnerB, "b", c => { calls.Add("g3"); return true; });
        using var m = ChatLinkRegistry.OnMod(OwnerB, "b", "m", c => { calls.Add("owner"); return false; }, false);

        Assert.True(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Mod, "m")));
        Assert.Equal(new[] { "owner", "g1", "g2" }, calls);

        calls.Clear();
        Assert.True(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Item)));
        Assert.Equal(new[] { "g1", "g2" }, calls);
    }

    [Fact]
    public void A_handled_owner_suppresses_the_rest_and_a_throwing_handler_is_not_handled()
    {
        var calls = new List<string>();
        using var bad = ChatLinkRegistry.OnClick(OwnerA, "a", c => throw new InvalidOperationException("boom"));
        using var g = ChatLinkRegistry.OnClick(OwnerA, "a", c => { calls.Add("g"); return false; });
        using var m = ChatLinkRegistry.OnMod(OwnerB, "b", "m", c => { calls.Add("owner"); return true; }, false);
        Assert.True(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Mod, "m")));
        Assert.Equal(new[] { "owner" }, calls);
        calls.Clear();
        Assert.False(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Web)));
        Assert.Equal(new[] { "g" }, calls);
    }

    [Fact]
    public void Owner_does_not_see_player_links_it_did_not_accept()
    {
        bool ownerRan = false;
        using var m = ChatLinkRegistry.OnMod(OwnerA, "a", "m", c => ownerRan = true, false);
        Assert.False(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Mod, "m", ChatLinkSource.Player)));
        Assert.False(ownerRan);
    }

    [Fact]
    public void One_plugin_per_owner_and_the_same_plugin_replaces()
    {
        var first = ChatLinkRegistry.OnMod(OwnerA, "plugin.a", "m", _ => true, false);
        Assert.Throws<InvalidOperationException>(() => ChatLinkRegistry.OnMod(OwnerB, "plugin.b", "M", _ => true, false));
        Assert.Throws<ArgumentException>(() => ChatLinkRegistry.OnMod(OwnerB, "plugin.b", "bad owner", _ => true, false));
        // A hot reload: the new generation claims before the old one let go; the old dispose must not drop the new claim.
        var second = ChatLinkRegistry.OnMod(OwnerB, "plugin.a", "m", _ => true, false);
        first.Dispose();
        Assert.True(ChatLinkRegistry.IsOwned("m"));
        second.Dispose();
        Assert.False(ChatLinkRegistry.IsOwned("m"));
    }

    [Fact]
    public void Unload_removes_the_handlers_and_changes_the_version()
    {
        int v0 = ChatLinkRegistry.Version;
        var g = ChatLinkRegistry.OnClick(OwnerA, "a", _ => true);
        var m = ChatLinkRegistry.OnMod(OwnerA, "a", "m", _ => true, false);
        Assert.True(ChatLinkRegistry.Version > v0);
        int v1 = ChatLinkRegistry.Version;
        g.Dispose();
        m.Dispose();
        g.Dispose();
        Assert.True(ChatLinkRegistry.Version > v1);
        Assert.False(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Mod, "m")));
        Assert.False(ChatLinkRegistry.IsOwned("m"));
        Assert.Equal(0, ChatLinkRegistry.GeneralCount);

        ChatLinkRegistry.OnClick(OwnerB, "b", _ => true);
        ChatLinkRegistry.OnMod(OwnerB, "b", "n", _ => true, false);
        Assert.Equal(2, ChatLinkRegistry.RemoveAll(OwnerB));
        Assert.False(ChatLinkRegistry.Dispatch(Click(ChatLinkKind.Item)));
    }

    [Fact]
    public void Click_carries_the_item_id_and_the_mod_payload()
    {
        var item = new ChatLinkClick(ChatLinkKind.Item, "-2128818978", "[Bone Sword]", ChatLinkSource.Player, "Bob", false, ChatChannel.Global, "");
        Assert.Equal(-2128818978, item.ItemPrefab);
        Assert.Equal("", item.Payload);
        var mod = new ChatLinkClick(ChatLinkKind.Mod, "7f3a", "Storage", ChatLinkSource.Server, "", false, ChatChannel.System, "", "satisvampory", "chest");
        Assert.Equal("7f3a", mod.Payload);
        Assert.True(mod.IsFromServer);
        Assert.Equal(0, mod.ItemPrefab);
    }

    // ---- item text ---------------------------------------------------------------------------------------

    [Fact]
    public void Item_text_names_the_item_and_never_shows_its_id()
    {
        Assert.Equal(("Bone Sword", "A crude sword."), ChatItemText.Content("Bone Sword", "<color=#fff>A crude</color>  sword."));
        Assert.Equal(("Bone Sword", ChatItemText.NoDescription), ChatItemText.Content(" Bone Sword ", null));
        Assert.Equal((ChatItemText.UnknownTitle, ChatItemText.UnknownBody), ChatItemText.Content(null, "ignored"));
        Assert.Equal(("Potion", "Restores … health."), ChatItemText.Content("Potion", "Restores {0} health."));
        Assert.Equal("a\nb", ChatItemText.Clean("a \n\n b"));
    }

    // ---- sample ------------------------------------------------------------------------------------------

    [Fact]
    public void Sample_link_line_escapes_what_the_caller_typed()
    {
        string line = SampleChatLinks.LinkLine(3, "evil] [mod:progenitor.sample:ping:9|fake");
        var found = ChatLinkToken.Find(line);
        Assert.Single(found);
        Assert.Equal("3", found[0].Payload);
        Assert.Equal(SampleChatLinks.LinkOwner, found[0].Owner);
        Assert.StartsWith("evil] [mod:", found[0].Text);
        Assert.Contains(SampleChatLinks.DefaultText, SampleChatLinks.LinkLine(1, "  "));
        Assert.Equal("Payload 3, on a line from the server.", SampleChatLinks.ClickNotice("3", ChatLinkSource.Server, "").Body);
        Assert.Equal("Payload 3, on a line from player Bob.", SampleChatLinks.ClickNotice("3", ChatLinkSource.Player, "Bob").Body);
    }
}
