using System;
using System.Collections.Generic;
using Progenitor.Commands;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Command routing MVP: owners, qualified fallback, collisions, administrator mappings,
/// single execution path, and lifetime. Shares the CommandRegistry collection because the
/// registry is process-wide static state.
/// </summary>
[Collection("CommandRegistry")]
public sealed class CommandRoutingTests : IDisposable
{
    public CommandRoutingTests()
    {
        CommandRegistry.ResetForTests();
    }

    public void Dispose()
    {
        CommandRegistry.ResetForTests();
    }

    [Fact]
    public void Distinct_owners_keep_short_and_qualified_routes()
    {
        OwnerA.Hits = 0;
        OwnerB.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        using var b = CommandRegistry.RegisterType(typeof(OwnerB), new object(), "mod.b");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_alpha"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_beta"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.b cr1_beta"));
        Assert.Equal(2, OwnerA.Hits);
        Assert.Equal(2, OwnerB.Hits);

        Assert.True(CommandRegistry.TryGetRoot("cr1_alpha", out var alpha));
        Assert.Equal("mod.a", alpha.OwnerId);
        Assert.Contains(".p mod.a cr1_alpha", CommandRegistry.FormatHelpLine(alpha), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Same_shortcut_conflict_is_order_independent(bool reverse)
    {
        SameA.Hits = 0;
        SameB.Hits = 0;
        IDisposable first, second;
        if (reverse)
        {
            first = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");
            second = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        }
        else
        {
            first = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
            second = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");
        }

        using (first)
        using (second)
        {
            Assert.False(CommandRegistry.TryGetRoot("cr1_same", out _));
            var ctx = new RecordingCtx();
            Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_same"));
            Assert.Equal(0, SameA.Hits);
            Assert.Equal(0, SameB.Hits);
            Assert.Single(ctx.Replies);
            Assert.Contains("mod.a", ctx.Replies[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains("mod.b", ctx.Replies[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".p mod.a cr1_same", ctx.Replies[0], StringComparison.OrdinalIgnoreCase);
            Assert.Contains(".p mod.b cr1_same", ctx.Replies[0], StringComparison.OrdinalIgnoreCase);

            var q = new RecordingCtx();
            Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(q, ".p mod.a cr1_same"));
            Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(q, ".p MOD.B cr1_same"));
            Assert.Equal(1, SameA.Hits);
            Assert.Equal(1, SameB.Hits);
        }
    }

    [Fact]
    public void Admin_assign_gives_the_shortcut_to_one_owner()
    {
        SameA.Hits = 0;
        SameB.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        using var b = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");

        Assert.True(CommandRoutingSettings.TryParse("p", "cr1_same = mod.a", "", "", out var settings, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(settings, out error), error);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_same"));
        Assert.Equal(1, SameA.Hits);
        Assert.Equal(0, SameB.Hits);
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.b cr1_same"));
        Assert.Equal(1, SameB.Hits);
    }

    [Fact]
    public void Admin_rename_and_disable_change_only_the_short_route()
    {
        OwnerA.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");

        Assert.True(CommandRoutingSettings.TryParse("p", "", "mod.a:cr1_alpha = spark", "", out var renamed, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(renamed, out error), error);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".spark"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_alpha"));
        Assert.Equal(2, OwnerA.Hits);

        Assert.True(CommandRoutingSettings.TryParse("p", "", "", "mod.a:cr1_alpha", out var disabled, out error), error);
        Assert.True(CommandRegistry.TryApplySettings(disabled, out error), error);
        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(ctx, ".spark"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_alpha"));
        Assert.Equal(3, OwnerA.Hits);
    }

    [Fact]
    public void Invalid_config_keeps_the_previous_table()
    {
        OwnerA.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        Assert.True(CommandRoutingSettings.TryParse("p", "", "mod.a:cr1_alpha = spark", "", out var renamed, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(renamed, out error), error);

        Assert.False(CommandRoutingSettings.TryParse("p p", "", "", "", out _, out error));
        Assert.Contains("single token", error, StringComparison.OrdinalIgnoreCase);
        Assert.False(CommandRoutingSettings.TryParse("p", "x = a\nx = b", "", "", out _, out error));
        Assert.Contains("both", error, StringComparison.OrdinalIgnoreCase);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".spark"));
        Assert.Equal(1, OwnerA.Hits);
        Assert.Equal("spark", CommandRegistry.Settings.Rename["mod.a:cr1_alpha"]);
    }

    [Fact]
    public void Shared_group_root_allows_distinct_literal_subcommands()
    {
        FindIron.Hits = 0;
        FindSteel.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(FindIron), new object(), "mod.a");
        using var b = CommandRegistry.RegisterType(typeof(FindSteel), new object(), "mod.b");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_find iron"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_find steel"));
        Assert.Equal(1, FindIron.Hits);
        Assert.Equal(1, FindSteel.Hits);
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_find iron"));
        Assert.Equal(2, FindIron.Hits);
    }

    [Fact]
    public void Literal_subcommand_beats_generic_argument_route()
    {
        GenericFind.Hits = 0;
        GenericFind.Last = null;
        LiteralIron.Hits = 0;
        using var g = CommandRegistry.RegisterType(typeof(GenericFind), new object(), "mod.g");
        using var l = CommandRegistry.RegisterType(typeof(LiteralIron), new object(), "mod.l");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_bag iron"));
        Assert.Equal(1, LiteralIron.Hits);
        Assert.Equal(0, GenericFind.Hits);

        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_bag copper"));
        Assert.Equal(1, GenericFind.Hits);
        Assert.Equal("copper", GenericFind.Last);
    }

    [Fact]
    public void Case_aliases_and_quoted_multiword_arguments_use_one_handler()
    {
        Say.Hits = 0;
        Say.Last = null;
        using var a = CommandRegistry.RegisterType(typeof(Say), new object(), "mod.a");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".CR1_SAY \"hello world\""));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_s \"hello world\""));
        Assert.Equal(2, Say.Hits);
        Assert.Equal("hello world", Say.Last);
    }

    [Fact]
    public void Permission_denial_and_bad_arguments_do_not_fall_through()
    {
        Secret.Hits = 0;
        NeedsInt.Hits = 0;
        using var s = CommandRegistry.RegisterType(typeof(Secret), new object(), "mod.a");
        using var n = CommandRegistry.RegisterType(typeof(NeedsInt), new object(), "mod.a");

        var player = new RecordingCtx { IsAdmin = false };
        Assert.Equal(CommandResult.Denied, CommandDispatcher.TryDispatch(player, ".cr1_secret"));
        Assert.Equal(0, Secret.Hits);
        Assert.Single(player.Replies);
        Assert.Contains("admin-only", player.Replies[0], StringComparison.OrdinalIgnoreCase);

        var bad = new RecordingCtx { IsAdmin = true };
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(bad, ".cr1_num nope"));
        Assert.Equal(0, NeedsInt.Hits);
        Assert.NotEmpty(bad.Replies);

        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(new RecordingCtx(), ".cr1_unowned"));
        Assert.Equal(CommandResult.NotACommand, CommandDispatcher.TryDispatch(new RecordingCtx(), "ordinary chat"));
    }

    [Fact]
    public void Unload_reinstall_and_swap_do_not_leave_stale_or_duplicate_handlers()
    {
        OwnerA.Hits = 0;
        var ownerOld = new object();
        var ownerNew = new object();
        var oldHandle = CommandRegistry.RegisterType(typeof(OwnerA), ownerOld, "mod.a");
        var newHandle = CommandRegistry.RegisterType(typeof(OwnerA), ownerNew, "mod.a");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(1, OwnerA.Hits);

        oldHandle.Dispose();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(2, OwnerA.Hits);

        CommandRegistry.UnregisterOwner(ownerOld);
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_alpha"));
        Assert.Equal(3, OwnerA.Hits);

        newHandle.Dispose();
        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));

        using var reinstalled = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(4, OwnerA.Hits);
    }

    [Fact]
    public void Removing_a_competitor_re_enables_the_shortcut()
    {
        SameA.Hits = 0;
        SameB.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        var b = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");

        Assert.False(CommandRegistry.TryGetRoot("cr1_same", out _));
        b.Dispose();
        Assert.True(CommandRegistry.TryGetRoot("cr1_same", out var bundle));
        Assert.Equal("mod.a", bundle.OwnerId);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_same"));
        Assert.Equal(1, SameA.Hits);
        Assert.Equal(0, SameB.Hits);
        Assert.Contains("fallback", CommandRegistry.FormatHelpLine(bundle), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Fallback_root_is_reserved_and_configurable()
    {
        OwnerA.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        using var p = CommandRegistry.RegisterType(typeof(ClaimsP), new object(), "mod.p");

        Assert.False(CommandRegistry.TryGetRoot("p", out _));
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p"));
        Assert.Equal(0, ClaimsP.Hits);

        Assert.True(CommandRoutingSettings.TryParse("cmd", "", "", "", out var settings, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(settings, out error), error);
        Assert.Equal("cmd", CommandRegistry.FallbackRoot);
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cmd mod.a cr1_alpha"));
        Assert.Equal(1, OwnerA.Hits);

        // The previous reserved token is released; a mod that claimed it can use it as a shortcut.
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p"));
        Assert.Equal(1, ClaimsP.Hits);
    }

    [Fact]
    public void Owner_id_is_the_BepInPlugin_GUID_not_the_display_title()
    {
        using var handle = CommandRegistry.RegisterType(typeof(GuidCmd), new TitledOwner());
        Assert.True(CommandRegistry.TryGetRoot("cr2_guid", out var bundle));
        Assert.Equal("stable.plugin.id", bundle.OwnerId);
        Assert.NotEqual("Pretty Display Title", bundle.OwnerId);
        Assert.DoesNotContain("TitledOwner", bundle.OwnerId, StringComparison.Ordinal);
        Assert.Contains(".p stable.plugin.id cr2_guid", CommandRegistry.FormatHelpLine(bundle), StringComparison.Ordinal);
    }

    [Fact]
    public void Two_plugins_sharing_an_id_collapse_same_path_and_keep_distinct_paths()
    {
        SharedAlpha.Hits = 0;
        SharedBeta.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(SharedAlpha), new object(), "mod.shared");
        using var b = CommandRegistry.RegisterType(typeof(SharedBeta), new object(), "mod.shared");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_shared_a"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_shared_b"));
        Assert.Equal(1, SharedAlpha.Hits);
        Assert.Equal(1, SharedBeta.Hits);

        // Same qualified identity from two live owners with the same id is Swap-style last-wins.
        SharedSameA.Hits = 0;
        SharedSameB.Hits = 0;
        using var first = CommandRegistry.RegisterType(typeof(SharedSameA), new object(), "mod.shared");
        using var second = CommandRegistry.RegisterType(typeof(SharedSameB), new object(), "mod.shared");
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_shared_same"));
        Assert.Equal(0, SharedSameA.Hits);
        Assert.Equal(1, SharedSameB.Hits);
        first.Dispose();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_shared_same"));
        Assert.Equal(2, SharedSameB.Hits);
    }

    [Fact]
    public void Blank_or_whitespace_fallback_root_defaults_to_p_space_is_rejected()
    {
        Assert.True(CommandRoutingSettings.TryParse("", "", "", "", out var blank, out var error), error);
        Assert.Equal("p", blank.FallbackRoot);
        Assert.True(CommandRoutingSettings.TryParse(" \t ", "", "", "", out var white, out error), error);
        Assert.Equal("p", white.FallbackRoot);
        Assert.False(CommandRoutingSettings.TryParse("p p", "", "", "", out _, out error));
        Assert.Contains("single token", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Invalid_assign_line_rejects_the_whole_change()
    {
        OwnerA.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        Assert.True(CommandRoutingSettings.TryParse("p", "", "mod.a:cr1_alpha = spark", "", out var renamed, out var error), error);
        Assert.True(CommandRegistry.TryApplySettings(renamed, out error), error);

        Assert.False(CommandRoutingSettings.TryParse("p", "cr1_alpha = mod.a; p = mod.a", "", "", out _, out error));
        Assert.Contains("reserved", error, StringComparison.OrdinalIgnoreCase);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".spark"));
        Assert.Equal(1, OwnerA.Hits);
        Assert.Equal("spark", CommandRegistry.Settings.Rename["mod.a:cr1_alpha"]);
    }

    [Fact]
    public void TryApplySettings_rejects_an_invalid_root_without_emptying_the_table()
    {
        OwnerA.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        var broken = new CommandRoutingSettings(
            "p p",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            new HashSet<string>(StringComparer.OrdinalIgnoreCase));
        Assert.False(CommandRegistry.TryApplySettings(broken, out var error));
        Assert.Contains("single token", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("p", CommandRegistry.FallbackRoot);

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(1, OwnerA.Hits);
    }

    [Fact]
    public void Case_differing_tokens_collide_and_do_not_execute()
    {
        SameA.Hits = 0;
        CaseSame.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        using var c = CommandRegistry.RegisterType(typeof(CaseSame), new object(), "mod.c");

        Assert.False(CommandRegistry.TryGetRoot("cr1_same", out _));
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".CR1_SAME"));
        Assert.Equal(0, SameA.Hits);
        Assert.Equal(0, CaseSame.Hits);
        Assert.Single(ctx.Replies);
    }

    [Fact]
    public void Trailing_separator_token_is_not_the_undotted_command()
    {
        OwnerA.Hits = 0;
        DottedAlpha.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(OwnerA), new object(), "mod.a");
        using var d = CommandRegistry.RegisterType(typeof(DottedAlpha), new object(), "mod.d");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha"));
        Assert.Equal(1, OwnerA.Hits);
        Assert.Equal(0, DottedAlpha.Hits);
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_alpha."));
        Assert.Equal(1, OwnerA.Hits);
        Assert.Equal(1, DottedAlpha.Hits);
    }

    [Fact]
    public void Alias_survives_a_primary_shortcut_conflict_and_help_names_it()
    {
        AliasOwner.Hits = 0;
        SameA.Hits = 0;
        using var alias = CommandRegistry.RegisterType(typeof(AliasOwner), new object(), "mod.alias");
        using var other = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_same"));
        Assert.Equal(0, AliasOwner.Hits);
        Assert.Equal(0, SameA.Hits);

        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_aka"));
        Assert.Equal(1, AliasOwner.Hits);
        Assert.True(CommandRegistry.TryGetRoot("cr2_aka", out var bundle));
        Assert.True(bundle.ShortcutsEnabled);
        Assert.Contains("cr2_aka", CommandRegistry.FormatHelpLine(bundle), StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".p mod.alias cr1_same", CommandRegistry.FormatHelpLine(bundle), StringComparison.Ordinal);
    }

    [Fact]
    public void Unload_drops_aliases_and_qualified_routes()
    {
        Say.Hits = 0;
        var handle = CommandRegistry.RegisterType(typeof(Say), new object(), "mod.a");
        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr1_s hi"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr1_say hi"));
        Assert.Equal(2, Say.Hits);

        handle.Dispose();
        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(ctx, ".cr1_s"));
        Assert.Equal(CommandResult.Unmatched, CommandDispatcher.TryDispatch(ctx, ".cr1_say"));
        var qualified = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(qualified, ".p mod.a cr1_say"));
        Assert.Equal(2, Say.Hits);
        Assert.Contains("Unknown command owner", string.Join("\n", qualified.Replies), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Competitor_removal_clears_conflict_diagnostics()
    {
        using var a = CommandRegistry.RegisterType(typeof(SameA), new object(), "mod.a");
        var b = CommandRegistry.RegisterType(typeof(SameB), new object(), "mod.b");
        Assert.Contains("cr1_same", string.Join("\n", CommandRegistry.Diagnostics), StringComparison.OrdinalIgnoreCase);
        b.Dispose();
        Assert.DoesNotContain("cr1_same", string.Join("\n", CommandRegistry.Diagnostics), StringComparison.OrdinalIgnoreCase);
        Assert.True(CommandRegistry.TryGetRoot("cr1_same", out var bundle));
        Assert.Contains("fallback", CommandRegistry.FormatHelpLine(bundle), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Same_group_and_subcommand_from_two_owners_is_ambiguous()
    {
        BoxA.Hits = 0;
        BoxB.Hits = 0;
        using var a = CommandRegistry.RegisterType(typeof(BoxA), new object(), "mod.a");
        using var b = CommandRegistry.RegisterType(typeof(BoxB), new object(), "mod.b");

        var ctx = new RecordingCtx();
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".cr2_box open"));
        Assert.Equal(0, BoxA.Hits);
        Assert.Equal(0, BoxB.Hits);
        Assert.Single(ctx.Replies);
        Assert.Contains("mod.a", ctx.Replies[0], StringComparison.OrdinalIgnoreCase);
        Assert.Contains(".p mod.a cr2_box open", ctx.Replies[0], StringComparison.Ordinal);

        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.a cr2_box open"));
        Assert.Equal(CommandResult.Executed, CommandDispatcher.TryDispatch(ctx, ".p mod.b cr2_box open"));
        Assert.Equal(1, BoxA.Hits);
        Assert.Equal(1, BoxB.Hits);
    }

    private sealed class RecordingCtx : ICommandContext
    {
        public bool IsAdmin { get; set; }
        public List<string> Replies { get; } = new();
        public void Reply(string text) => Replies.Add(text ?? "");
    }

    private static class OwnerA
    {
        public static int Hits;
        [Command("cr1_alpha")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class OwnerB
    {
        public static int Hits;
        [Command("cr1_beta")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class SameA
    {
        public static int Hits;
        [Command("cr1_same")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class SameB
    {
        public static int Hits;
        [Command("cr1_same")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    [CommandGroup("cr1_find")]
    private static class FindIron
    {
        public static int Hits;
        [Command("iron")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    [CommandGroup("cr1_find")]
    private static class FindSteel
    {
        public static int Hits;
        [Command("steel")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class GenericFind
    {
        public static int Hits;
        public static string? Last;
        [Command("cr1_bag")]
        public static void Run(ICommandContext ctx, string what)
        {
            Hits++;
            Last = what;
        }
    }

    [CommandGroup("cr1_bag")]
    private static class LiteralIron
    {
        public static int Hits;
        [Command("iron")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class Say
    {
        public static int Hits;
        public static string? Last;
        [Command("cr1_say", shortHand: "cr1_s")]
        public static void Run(ICommandContext ctx, string text)
        {
            Hits++;
            Last = text;
        }
    }

    private static class Secret
    {
        public static int Hits;
        [Command("cr1_secret", adminOnly: true)]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class NeedsInt
    {
        public static int Hits;
        [Command("cr1_num")]
        public static void Run(ICommandContext ctx, int n) => Hits++;
    }

    private static class ClaimsP
    {
        public static int Hits;
        [Command("p")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    [AttributeUsage(AttributeTargets.Class)]
    private sealed class BepInPlugin : Attribute
    {
        public string GUID { get; }
        public string Name { get; }
        public BepInPlugin(string guid, string name)
        {
            GUID = guid;
            Name = name;
        }
    }

    [@BepInPlugin("stable.plugin.id", "Pretty Display Title")]
    private sealed class TitledOwner
    {
        public override string ToString() => "Not The Id";
    }

    private static class GuidCmd
    {
        [Command("cr2_guid")]
        public static void Run(ICommandContext ctx) { }
    }

    private static class SharedAlpha
    {
        public static int Hits;
        [Command("cr2_shared_a")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class SharedBeta
    {
        public static int Hits;
        [Command("cr2_shared_b")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class SharedSameA
    {
        public static int Hits;
        [Command("cr2_shared_same")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class SharedSameB
    {
        public static int Hits;
        [Command("cr2_shared_same")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class CaseSame
    {
        public static int Hits;
        [Command("CR1_SAME")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class DottedAlpha
    {
        public static int Hits;
        [Command("cr1_alpha.")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    private static class AliasOwner
    {
        public static int Hits;
        [Command("cr1_same", shortHand: "cr2_aka")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    [CommandGroup("cr2_box")]
    private static class BoxA
    {
        public static int Hits;
        [Command("open")]
        public static void Run(ICommandContext ctx) => Hits++;
    }

    [CommandGroup("cr2_box")]
    private static class BoxB
    {
        public static int Hits;
        [Command("open")]
        public static void Run(ICommandContext ctx) => Hits++;
    }
}
