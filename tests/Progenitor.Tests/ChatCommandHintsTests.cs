using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Chat;
using Progenitor.Commands;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Chat command hints: the per-player catalog built from the routing table, the <c>'PrCh'</c> pages, the client's
/// request state machine and the matching the hint line uses. Shares the CommandRegistry collection because the
/// registry is process-wide static state.
/// </summary>
[Collection("CommandRegistry")]
public sealed class ChatCommandHintsTests : IDisposable
{
    public ChatCommandHintsTests()
    {
        CommandRegistry.ResetForTests();
        ChatCommandHints.Reset();
    }

    public void Dispose()
    {
        CommandRegistry.ResetForTests();
        ChatCommandHints.Reset();
        ChatCommandHints.Send = null;
    }

    // ---- catalog ----

    [Fact]
    public void A_non_admin_never_sees_an_admin_only_command_or_form()
    {
        using var reg = CommandRegistry.RegisterType(typeof(Mixed), new object(), "mod.h");

        var player = CommandHintCatalog.Build(isAdmin: false);
        Assert.DoesNotContain(player, e => e.Command == ".hx_secret");
        var tool = Assert.Single(player, e => e.Command == ".hx_tool");
        Assert.Equal(".hx_tool <name>", tool.Usage);
        Assert.Equal("Player form.", tool.Help);

        var admin = CommandHintCatalog.Build(isAdmin: true);
        Assert.Contains(admin, e => e.Command == ".hx_secret");
        var adminTool = Assert.Single(admin, e => e.Command == ".hx_tool");
        Assert.Contains(".hx_tool <name>", adminTool.Usage, StringComparison.Ordinal);
        Assert.Contains(".hx_tool <name> <count>", adminTool.Usage, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_is_the_shortcut_when_published_and_the_qualified_form_otherwise()
    {
        using var a = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        using var b = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");
        using var g = CommandRegistry.RegisterType(typeof(Grouped), new object(), "mod.g");

        var list = CommandHintCatalog.Build(isAdmin: false);
        // The collision disables the shortcut for both owners: only the qualified routes are listed.
        Assert.DoesNotContain(list, e => e.Command == ".hx_same");
        Assert.Contains(list, e => e.Command == ".p mod.a hx_same");
        Assert.Contains(list, e => e.Command == ".p mod.b hx_same");
        Assert.Contains(list, e => e.Command == ".hxg run");

        Assert.True(CommandRoutingSettings.TryParse("p", "", "", "mod.g:hxg/run", out var disabled, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(disabled, out error), error);
        list = CommandHintCatalog.Build(isAdmin: false);
        Assert.DoesNotContain(list, e => e.Command == ".hxg run");
        var run = Assert.Single(list, e => e.Command == ".p mod.g hxg run");
        Assert.Equal(".p mod.g hxg run [rest...]", run.Usage);

        // Every listed text dispatches.
        foreach (var entry in list)
            Assert.NotEqual(CommandResult.Unmatched, CommandDispatcher.TryDispatch(new Ctx(), entry.Command));
    }

    [Fact]
    public void A_renamed_shortcut_is_listed_under_its_new_name()
    {
        using var reg = CommandRegistry.RegisterType(typeof(Mixed), new object(), "mod.h");
        Assert.True(CommandRoutingSettings.TryParse("p", "", "mod.h:hx_tool = hx_spark", "", out var renamed, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(renamed, out error), error);
        var list = CommandHintCatalog.Build(isAdmin: false);
        Assert.Contains(list, e => e.Command == ".hx_spark");
        Assert.DoesNotContain(list, e => e.Command == ".hx_tool");
    }

    [Fact]
    public void Help_is_one_line_and_clipped()
    {
        using var reg = CommandRegistry.RegisterType(typeof(Wordy), new object(), "mod.w");
        var entry = Assert.Single(CommandHintCatalog.Build(isAdmin: false), e => e.Command == ".hx_wordy");
        Assert.InRange(entry.Help.Length, CommandHintCatalog.MaxHelpChars - 4, CommandHintCatalog.MaxHelpChars);
        Assert.EndsWith("...", entry.Help, StringComparison.Ordinal);
        Assert.DoesNotContain("\n", entry.Help, StringComparison.Ordinal);
        Assert.StartsWith("First line second line", entry.Help, StringComparison.Ordinal);
    }

    [Fact]
    public void The_catalog_is_sorted_and_capped()
    {
        var bundles = new List<CommandBundle>();
        var overload = MakeOverload();
        for (int i = 0; i < 250; i++)
        {
            var bundle = new CommandBundle(new object(), "mod.z", new object(), "", null, "z" + (249 - i).ToString("D3"),
                new List<string>(), "help", new List<CommandOverload> { overload });
            bundle.ShortcutsEnabled = true;
            bundle.EffectiveShortcut = bundle.Name;
            bundles.Add(bundle);
        }

        var list = CommandHintCatalog.Build(bundles, isAdmin: false, ".", "p");
        Assert.Equal(CommandHintCatalog.MaxEntries, list.Count);
        Assert.Equal(".z000", list[0].Command);
        Assert.Equal(".z199", list[^1].Command);
    }

    [Fact]
    public void Generation_moves_on_every_table_change()
    {
        int before = CommandRegistry.Generation;
        using (CommandRegistry.RegisterType(typeof(Mixed), new object(), "mod.h"))
        {
            Assert.NotEqual(before, CommandRegistry.Generation);
            before = CommandRegistry.Generation;
        }

        Assert.NotEqual(before, CommandRegistry.Generation);
    }

    // ---- wire ----

    [Fact]
    public void Hello_and_request_round_trip_and_do_not_cross_decode()
    {
        var hello = ChatCommandHintsWire.EncodeHello(42);
        var request = ChatCommandHintsWire.EncodeRequest(7);
        Assert.True(ChatCommandHintsWire.TryDecodeHello(hello, out int stamp));
        Assert.Equal(42, stamp);
        Assert.True(ChatCommandHintsWire.TryDecodeRequest(request, out int nonce));
        Assert.Equal(7, nonce);
        Assert.False(ChatCommandHintsWire.TryDecodeRequest(hello, out _));
        Assert.False(ChatCommandHintsWire.TryDecodeHello(request, out _));
        Assert.False(ChatCommandHintsWire.TryDecodePage(request, out _));
    }

    [Fact]
    public void A_full_catalog_splits_into_pages_under_the_limit_and_reassembles_in_order()
    {
        var entries = BigEntries(CommandHintCatalog.MaxEntries);
        var pages = ChatCommandHintsWire.EncodePages(5, 9, entries);
        Assert.True(pages.Count > 1);
        Assert.All(pages, p => Assert.True(p.Length <= ChatCommandHintsWire.MaxPacketBytes));

        var back = new List<ChatCommandHint>();
        for (int i = 0; i < pages.Count; i++)
        {
            Assert.True(ChatCommandHintsWire.TryDecodePage(pages[i], out var page));
            Assert.Equal(5, page.Nonce);
            Assert.Equal(9, page.Stamp);
            Assert.Equal(i, page.Index);
            Assert.Equal(pages.Count, page.Count);
            back.AddRange(page.Entries);
        }

        Assert.Equal(entries.Select(e => e.Command), back.Select(e => e.Command));
        Assert.Equal(entries.Select(e => e.Usage), back.Select(e => e.Usage));
        Assert.Equal(entries.Select(e => e.Help), back.Select(e => e.Help));
    }

    [Fact]
    public void An_empty_catalog_is_one_empty_page()
    {
        var pages = ChatCommandHintsWire.EncodePages(1, 2, new List<CommandHintEntry>());
        var only = Assert.Single(pages);
        Assert.True(ChatCommandHintsWire.TryDecodePage(only, out var page));
        Assert.Empty(page.Entries);
        Assert.Equal(1, page.Count);
    }

    [Fact]
    public void A_truncated_or_padded_page_is_dropped_whole()
    {
        var page = ChatCommandHintsWire.EncodePages(1, 1, new List<CommandHintEntry> { new(".a", ".a", "x") })[0];
        Assert.False(ChatCommandHintsWire.TryDecodePage(page.AsSpan(0, page.Length - 1), out _));
        var padded = new byte[page.Length + 1];
        page.CopyTo(padded, 0);
        Assert.False(ChatCommandHintsWire.TryDecodePage(padded, out _));
        var badVersion = (byte[])page.Clone();
        badVersion[0] = 99;
        Assert.False(ChatCommandHintsWire.TryDecodePage(badVersion, out _));
    }

    [Fact]
    public void The_server_rate_limit_answers_one_request_per_interval_per_user()
    {
        var limit = new ChatCommandHintsRateLimit();
        Assert.True(limit.Allow(1, 10));
        Assert.False(limit.Allow(1, 11));
        Assert.True(limit.Allow(2, 11));
        Assert.True(limit.Allow(1, 10 + ChatCommandHintsRateLimit.MinIntervalSeconds));
        limit.Forget(1);
        Assert.True(limit.Allow(1, 12.5));
    }

    // ---- client state ----

    [Fact]
    public void Nothing_is_sent_before_the_servers_hello()
    {
        var sent = Wire(out var clock);
        for (int i = 0; i < 10; i++)
        {
            clock[0] += 60;
            ChatCommandHints.RequestIfNeeded();
        }

        Assert.Empty(sent);
        Assert.False(ChatCommandHints.Available);
        Assert.Empty(ChatCommandHints.Match(".", 5));
    }

    [Fact]
    public void One_request_per_connection_then_again_only_when_the_stamp_changes()
    {
        var sent = Wire(out var clock);
        ChatCommandHints.OnHello(3);
        ChatCommandHints.RequestIfNeeded();
        ChatCommandHints.RequestIfNeeded();
        int nonce = Assert.Single(sent);
        Answer(nonce, 3, new CommandHintEntry(".help", ".help [command]", "List commands."));
        Assert.True(ChatCommandHints.Available);
        Assert.Equal(1, ChatCommandHints.Count);

        clock[0] += 120;
        ChatCommandHints.RequestIfNeeded();
        Assert.Single(sent);

        ChatCommandHints.OnHello(4);
        ChatCommandHints.RequestIfNeeded();
        Assert.Equal(2, sent.Count);
        // The stale catalog is kept while the new one is on its way.
        Assert.True(ChatCommandHints.Available);
    }

    [Fact]
    public void Requests_are_spaced_time_out_and_give_up_after_the_attempt_limit()
    {
        var sent = Wire(out var clock);
        ChatCommandHints.OnHello(1);
        ChatCommandHints.RequestIfNeeded();
        clock[0] += ChatCommandHints.MinRequestIntervalSeconds;
        ChatCommandHints.RequestIfNeeded();
        Assert.Single(sent); // still waiting for the answer

        for (int i = 0; i < 10; i++)
        {
            clock[0] += ChatCommandHints.TimeoutSeconds;
            ChatCommandHints.RequestIfNeeded();
        }

        Assert.Equal(ChatCommandHints.MaxAttemptsPerStamp, sent.Count);
        Assert.False(ChatCommandHints.Available);
    }

    [Fact]
    public void Pages_of_another_request_are_ignored_and_a_partial_catalog_is_never_shown()
    {
        var sent = Wire(out _);
        ChatCommandHints.OnHello(1);
        ChatCommandHints.RequestIfNeeded();
        int nonce = Assert.Single(sent);

        var pages = ChatCommandHintsWire.EncodePages(nonce, 1, BigEntries(60));
        Assert.True(pages.Count > 1);
        var foreign = ChatCommandHintsWire.EncodePages(nonce + 1, 1, BigEntries(1));
        Assert.True(ChatCommandHintsWire.TryDecodePage(foreign[0], out var stray));
        ChatCommandHints.OnPage(stray);
        Assert.False(ChatCommandHints.Available);

        for (int i = pages.Count - 1; i >= 1; i--)
        {
            Assert.True(ChatCommandHintsWire.TryDecodePage(pages[i], out var page));
            ChatCommandHints.OnPage(page);
            Assert.False(ChatCommandHints.Available);
        }

        Assert.True(ChatCommandHintsWire.TryDecodePage(pages[0], out var first));
        ChatCommandHints.OnPage(first);
        Assert.True(ChatCommandHints.Available);
        Assert.Equal(60, ChatCommandHints.Count);
    }

    [Fact]
    public void Reset_forgets_the_server()
    {
        var sent = Wire(out _);
        ChatCommandHints.OnHello(1);
        ChatCommandHints.RequestIfNeeded();
        Answer(Assert.Single(sent), 1, new CommandHintEntry(".help", "", ""));
        Assert.True(ChatCommandHints.Available);

        ChatCommandHints.Reset();
        Assert.False(ChatCommandHints.Available);
        Assert.False(ChatCommandHints.ServerHasHints);
        ChatCommandHints.RequestIfNeeded();
        Assert.Single(sent);
    }

    // ---- matching ----

    private static readonly List<ChatCommandHint> Catalog = new()
    {
        new(".help", ".help [command]", ""),
        new(".heal", ".heal [amount]", ""),
        new(".home", ".home", ""),
        new(".army summon", ".army summon <count>", ""),
        new(".army", ".army", ""),
        new(".p mod.a hx_same", "", ""),
    };

    [Fact]
    public void Prefix_matches_ignore_case_shortest_first()
    {
        var m = ChatCommandHints.MatchIn(Catalog, ".HE", 10);
        Assert.Equal(new[] { ".heal", ".help" }, m.Select(h => h.Command));
        m = ChatCommandHints.MatchIn(Catalog, ".h", 2);
        Assert.Equal(new[] { ".heal", ".help" }, m.Select(h => h.Command));
    }

    [Fact]
    public void The_named_command_comes_first_and_stays_while_arguments_are_typed()
    {
        var m = ChatCommandHints.MatchIn(Catalog, ".army", 10);
        Assert.Equal(new[] { ".army", ".army summon" }, m.Select(h => h.Command));

        m = ChatCommandHints.MatchIn(Catalog, ".army  summon 5", 10);
        Assert.Equal(new[] { ".army summon", ".army" }, m.Select(h => h.Command));

        m = ChatCommandHints.MatchIn(Catalog, "  .help heal", 10);
        Assert.Equal(".help", Assert.Single(m).Command);
    }

    [Fact]
    public void Nothing_matches_an_unknown_line_an_empty_line_or_a_zero_max()
    {
        Assert.Empty(ChatCommandHints.MatchIn(Catalog, ".zzz", 10));
        Assert.Empty(ChatCommandHints.MatchIn(Catalog, "", 10));
        Assert.Empty(ChatCommandHints.MatchIn(Catalog, null, 10));
        Assert.Empty(ChatCommandHints.MatchIn(Catalog, ".", 0));
        Assert.Equal(Catalog.Count, ChatCommandHints.MatchIn(Catalog, ".", 100).Count);
        Assert.Empty(ChatCommandHints.MatchIn(Catalog, ".helpx", 10));
    }

    // ---- helpers ----

    private static List<int> Wire(out double[] clock)
    {
        var sent = new List<int>();
        var time = new[] { 1000.0 };
        clock = time;
        ChatCommandHints.Now = () => time[0];
        ChatCommandHints.Send = bytes =>
        {
            Assert.True(ChatCommandHintsWire.TryDecodeRequest(bytes, out int nonce));
            sent.Add(nonce);
            return true;
        };
        return sent;
    }

    private static void Answer(int nonce, int stamp, params CommandHintEntry[] entries)
    {
        foreach (var bytes in ChatCommandHintsWire.EncodePages(nonce, stamp, entries))
        {
            Assert.True(ChatCommandHintsWire.TryDecodePage(bytes, out var page));
            ChatCommandHints.OnPage(page);
        }
    }

    private static List<CommandHintEntry> BigEntries(int count)
    {
        var list = new List<CommandHintEntry>();
        for (int i = 0; i < count; i++)
        {
            // Non-ASCII text so the byte budget, not the character count, decides the page split.
            list.Add(new CommandHintEntry(
                ".c" + i.ToString("D3") + new string('é', CommandHintCatalog.MaxCommandChars - 5),
                new string('ü', CommandHintCatalog.MaxUsageChars),
                new string('ß', CommandHintCatalog.MaxHelpChars)));
        }

        return list;
    }

    private static CommandOverload MakeOverload()
    {
        var method = typeof(Plain).GetMethod(nameof(Plain.Run))!;
        var attr = new CommandAttribute("x");
        return new CommandOverload(method, null, attr, Array.Empty<System.Reflection.ParameterInfo>(), false);
    }

    private sealed class Ctx : ICommandContext
    {
        public bool IsAdmin { get; set; }
        public void Reply(string text) { }
    }

    private static class Plain
    {
        public static void Run(ICommandContext ctx) { }
    }

    private static class Mixed
    {
        [Command("hx_tool", description: "Player form.")]
        public static void Tool(ICommandContext ctx, string name) { }

        [Command("hx_tool", adminOnly: true, description: "Admin form.")]
        public static void ToolCount(ICommandContext ctx, string name, int count) { }

        [Command("hx_secret", adminOnly: true, description: "Admins only.")]
        public static void Secret(ICommandContext ctx) { }
    }

    private static class SameA
    {
        [Command("hx_same")]
        public static void Run(ICommandContext ctx) { }
    }

    private static class SameB
    {
        [Command("hx_same")]
        public static void Run(ICommandContext ctx) { }
    }

    [CommandGroup("hxg")]
    private static class Grouped
    {
        [Command("run")]
        public static void Run(ICommandContext ctx, [Remainder] string rest = "") { }
    }

    private static class Wordy
    {
        [Command("hx_wordy", description: "First line\nsecond   line " + "and a great deal more text that goes on and on well past the one hundred and twenty character cap for one help line.")]
        public static void Run(ICommandContext ctx) { }
    }
}
