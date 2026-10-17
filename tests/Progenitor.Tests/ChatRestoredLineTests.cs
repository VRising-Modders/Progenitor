using System;
using System.Collections.Generic;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Restored chat history and the item-name lookup, game-free. History is restored when the chat window appears, which
/// can be before the game's item data loads; the first draw of those lines then has no names. A miss must not stick:
/// <see cref="ChatNameCache"/> retries, raises its version when a name arrives, and the redraw shows it.
/// </summary>
public sealed class ChatRestoredLineTests
{
    private const int Copper = 862477668;

    /// <summary>A stand-in for the game's item data: empty until <see cref="Loaded"/>.</summary>
    private sealed class FakeItemData
    {
        internal bool Loaded;
        internal int Lookups;
        internal readonly Dictionary<int, string> Names = new() { [Copper] = "Copper Ingot", [42] = "Bone Sword" };

        internal string? Lookup(int id)
        {
            Lookups++;
            return Loaded && Names.TryGetValue(id, out var n) ? n : null;
        }
    }

    [Fact]
    public void A_miss_is_retried_after_its_wait_and_raises_the_version_when_it_resolves()
    {
        var data = new FakeItemData();
        var cache = new ChatNameCache(data.Lookup);
        Assert.Null(cache.Get(Copper, now: 0));
        Assert.Equal(1, cache.MissingCount);
        int v0 = cache.Version;

        // Not due yet: no second lookup.
        Assert.Null(cache.Get(Copper, now: 0.5));
        Assert.False(cache.Poll(now: 0.5));
        Assert.Equal(1, data.Lookups);

        // Due, still not loaded: the wait doubles.
        Assert.False(cache.Poll(now: 1.0));
        Assert.Equal(2, data.Lookups);
        Assert.False(cache.Poll(now: 2.5));
        Assert.Equal(2, data.Lookups);

        data.Loaded = true;
        Assert.True(cache.Poll(now: 3.0));
        Assert.True(cache.Version > v0);
        Assert.Equal(0, cache.MissingCount);
        Assert.Equal("Copper Ingot", cache.Get(Copper, now: 3.0));
        Assert.Equal(3, data.Lookups); // a hit is not looked up again
    }

    [Fact]
    public void An_unknown_id_backs_off_to_the_cap_and_never_raises_the_version()
    {
        var data = new FakeItemData { Loaded = true };
        var cache = new ChatNameCache(data.Lookup);
        Assert.Null(cache.Get(7, now: 0));
        int v0 = cache.Version;
        double now = 0;
        for (int i = 0; i < 20; i++)
        {
            now += ChatNameCache.MaxRetrySeconds;
            Assert.False(cache.Poll(now));
        }

        Assert.Equal(v0, cache.Version);
        Assert.Equal(21, data.Lookups); // one per capped wait, not one per frame
    }

    [Fact]
    public void A_throwing_lookup_is_a_miss()
    {
        var cache = new ChatNameCache(_ => throw new InvalidOperationException("not ready"));
        Assert.Null(cache.Get(1, now: 0));
        Assert.Equal(1, cache.MissingCount);
    }

    [Fact]
    public void Clear_forgets_hits_and_raises_the_version()
    {
        var data = new FakeItemData { Loaded = true };
        var cache = new ChatNameCache(data.Lookup);
        Assert.Equal("Bone Sword", cache.Get(42, 0));
        int v0 = cache.Version;
        cache.Clear();
        Assert.True(cache.Version > v0);
        data.Loaded = false;
        Assert.Null(cache.Get(42, 0));
    }

    [Fact]
    public void Restored_lines_keep_their_tokens_and_show_names_once_the_item_data_loads()
    {
        // A Satisvampory-style server line, saved and read back as the history file does.
        var log = new ChatLog();
        string text = "  5,000 [item:" + Copper + "] at [loc:-1804,-1697]";
        log.Add(new ChatRecord
        {
            Kind = ChatKind.System, User = "", Text = text, Full = text, TimeUtc = new DateTime(2026, 10, 17, 12, 0, 0, DateTimeKind.Utc),
            Source = ChatLinkSource.Server,
        }, collapse: false);
        var restored = ChatLog.Deserialize(log.Serialize(10), 10);
        var r = Assert.Single(restored);
        Assert.True(r.Restored);
        Assert.Equal(text, r.Text); // the token is saved as written, never as drawn

        var data = new FakeItemData();
        var cache = new ChatNameCache(data.Lookup);
        double now = 0;
        var old = ChatFormat.ItemNameResolver;
        try
        {
            ChatFormat.ItemNameResolver = id => cache.Get(id, now);
            var options = new ChatFormat.Options();

            // Drawn before the item data loads: the token as written (the location needs no game data).
            string first = ChatFormat.Render(r, options, out var spans);
            Assert.Contains("[item:" + Copper + "]", first);
            Assert.Contains("[-1804, -1697]", first);
            Assert.Equal(2, spans.Count);
            int drawnVersion = cache.Version;

            // The data loads; the view's per-frame poll finds the name and the version change asks for a redraw.
            data.Loaded = true;
            now = 1.0;
            Assert.True(cache.Poll(now));
            Assert.NotEqual(drawnVersion, cache.Version);

            string second = ChatFormat.Render(r, options, out spans);
            Assert.Contains("[Copper Ingot]", second);
            Assert.DoesNotContain("[item:", second);
            Assert.StartsWith("<alpha=#99>", second); // still drawn as a restored line
            Assert.Equal(ChatSpanKind.Item, ChatFormat.SpanForLink("i:0", spans)!.Value.Kind);
        }
        finally { ChatFormat.ItemNameResolver = old; }
    }

    [Fact]
    public void A_restored_player_line_resolves_the_same_way()
    {
        var r = new ChatRecord { Kind = ChatKind.Global, User = "A", Text = "trade [item:42]?", Restored = true };
        var data = new FakeItemData();
        var cache = new ChatNameCache(data.Lookup);
        double now = 0;
        var old = ChatFormat.ItemNameResolver;
        try
        {
            ChatFormat.ItemNameResolver = id => cache.Get(id, now);
            Assert.Contains("[item:42]", ChatFormat.Render(r, new ChatFormat.Options(), out _));
            data.Loaded = true;
            now = 5;
            Assert.True(cache.Poll(now));
            Assert.Contains("[Bone Sword]", ChatFormat.Render(r, new ChatFormat.Options(), out _));
        }
        finally { ChatFormat.ItemNameResolver = old; }
    }
}
