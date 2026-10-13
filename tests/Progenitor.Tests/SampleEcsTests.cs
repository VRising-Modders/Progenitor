using System;
using System.Globalization;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job J14 (RD6). Game-free coverage of <c>.ecs</c>: token and allowlist parsing, and every reply
/// line an operator is told to look for. The two calls being exampled — <c>Query.Build</c> with an
/// explicit dispose and <c>Singletons.TryGetEntity</c> / <c>TryGetRawRW</c> — need a live
/// <c>EntityManager</c> and are behind <c>!PROGENITOR_TESTS</c>, so the dispose and the
/// <c>includeDisabled</c> flags are read out of the source the way
/// <see cref="SampleGroupTests"/> reads the group attribute.
/// </summary>
[Collection("PluginLogger")]
public class SampleEcsTests : IDisposable
{
    public SampleEcsTests() => SampleLog.Clear();

    public void Dispose() => SampleLog.Clear();

    [Theory]
    [InlineData("", SampleEcs.Kind.Users)]
    [InlineData("   ", SampleEcs.Kind.Users)]
    [InlineData("users", SampleEcs.Kind.Users)]
    [InlineData(" USERS ", SampleEcs.Kind.Users)]
    [InlineData("singleton", SampleEcs.Kind.Singleton)]
    [InlineData("Singleton", SampleEcs.Kind.Singleton)]
    internal void Known_tokens_parse(string token, SampleEcs.Kind expected)
    {
        Assert.True(SampleEcs.TryParse(token, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("user")]
    [InlineData("players")]
    [InlineData("singletons")]
    public void An_unknown_token_is_refused_so_the_command_replies_usage(string token)
    {
        Assert.False(SampleEcs.TryParse(token, out _));
        Assert.Equal("Usage: .ecs users | .ecs singleton [name] | .ecs tag", SampleEcs.UsageReply);
    }

    [Fact]
    public void No_name_means_the_singleton_Core_itself_reads()
    {
        Assert.True(SampleEcs.TryParseSingleton("", out string name));
        Assert.Equal("ServerRuntimeSettings", name);
        Assert.Equal(SampleEcs.DefaultSingleton, name);
    }

    /// <summary>
    /// The declared spelling comes back, not the caller's, so the reply, the log and the
    /// allowlist all agree on capitalization whatever was typed in chat.
    /// </summary>
    [Fact]
    public void A_known_name_matches_case_insensitively_and_returns_the_declared_spelling()
    {
        Assert.True(SampleEcs.TryParseSingleton("usermutesingleton", out string name));
        Assert.Equal("UserMuteSingleton", name);
    }

    /// <summary>
    /// A chat-supplied type name is player input. The allowlist is the whole safety story here,
    /// so an unknown name must be a refusal and not a lookup.
    /// </summary>
    [Theory]
    [InlineData("Translation")]
    [InlineData("ProjectM.ServerRuntimeSettings")]
    [InlineData("../../etc")]
    public void An_unlisted_name_is_refused_with_the_list(string asked)
    {
        Assert.False(SampleEcs.TryParseSingleton(asked, out string name));
        Assert.Equal("", name);

        string reply = SampleEcs.UnknownSingletonReply(asked);
        Assert.Contains("is not one this sample resolves", reply, StringComparison.Ordinal);
        foreach (var known in SampleEcs.KnownSingletons)
            Assert.Contains(known, reply, StringComparison.Ordinal);
    }

    [Fact]
    public void The_refusal_cannot_forge_a_log_line_out_of_the_name_it_echoes()
    {
        string reply = SampleEcs.UnknownSingletonReply("x\nSample ecs: forged");
        Assert.DoesNotContain("\n", reply, StringComparison.Ordinal);
    }

    /// <summary>
    /// The census exists to make interop trap 4 visible: the reply has to name the gap in words,
    /// and (job RD9) must not explain it as the offline roster - a disconnect never disables a
    /// <c>User</c> entity, so a disabled one has some other cause.
    /// </summary>
    [Fact]
    public void The_users_reply_names_the_gap_between_the_two_queries_without_blaming_the_offline_roster()
    {
        var lines = SampleEcs.UsersLines(withDisabled: 7, withoutDisabled: 2, connected: 2, offline: 5);

        Assert.Contains(lines, l => l.Contains("7 entities with includeDisabled=true, 2 with the default", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("The 5 missing from the default query are disabled entities", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("interop trap 4", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("None of them is disabled merely for being offline", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("2 connected, 5 offline", StringComparison.Ordinal));
    }

    /// <summary>
    /// Equal counts with nobody offline are not evidence that the trap does not exist, and (job
    /// RD9) the reply may not promise that logging out would create a gap: it would not.
    /// </summary>
    [Fact]
    public void Equal_counts_with_nobody_offline_are_not_reported_as_the_default_query_being_safe()
    {
        var lines = SampleEcs.UsersLines(withDisabled: 1, withoutDisabled: 1, connected: 1, offline: 0);

        Assert.Contains(lines, l => l.Contains("They agree, and none of these users is offline", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("They would still agree if some were", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("units out of player range", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("would still hide an offline player's User entity", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("interop trap 4", StringComparison.Ordinal));
    }

    /// <summary>
    /// R64 finding 6, and the live shape (evidence 208: <c>withDisabled=2 default=2 connected=1
    /// offline=1</c>). Equal counts <em>with</em> an offline user mean that user's <c>User</c>
    /// entity is not disabled, so the reply may not claim none is loaded — the very next line of
    /// the same reply counts it. Job RD9: that is the <b>permanent</b> state, not a pending one,
    /// so the reply may not describe it as a disable that has not happened yet.
    /// </summary>
    [Fact]
    public void Equal_counts_with_an_offline_user_say_that_entity_is_never_disabled_by_the_disconnect()
    {
        var lines = SampleEcs.UsersLines(withDisabled: 2, withoutDisabled: 2, connected: 1, offline: 1);

        Assert.Contains(lines, l => l.Contains("The counts agree even though 1 of these users is offline", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("that is the normal state, not a pending one", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("a disconnect never disables a User entity", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("has not disabled yet", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("1 connected, 1 offline", StringComparison.Ordinal));

        // The sentence the live run contradicted must not be reachable in this state.
        Assert.DoesNotContain(lines, l => l.Contains("no offline player's User entity is loaded", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("none of these users is offline", StringComparison.Ordinal));
    }

    /// <summary>
    /// A gap wider than the offline tally is the two sources disagreeing (the engine's
    /// <c>Disabled</c> state against <c>User.IsConnected</c>), so the sentence says that instead of
    /// calling every hidden entity an offline player. A gap covered by the offline tally does not
    /// get the caveat.
    /// </summary>
    [Fact]
    public void A_gap_wider_than_the_offline_tally_is_flagged_rather_than_explained_away()
    {
        Assert.Contains("More are hidden than are offline", SampleEcs.Agreement(hidden: 5, offline: 2), StringComparison.Ordinal);
        Assert.DoesNotContain("More are hidden than are offline", SampleEcs.Agreement(hidden: 2, offline: 5), StringComparison.Ordinal);
        Assert.DoesNotContain("More are hidden than are offline", SampleEcs.Agreement(hidden: 5, offline: 5), StringComparison.Ordinal);
    }

    /// <summary>
    /// The three states are exclusive and each is derived from the counts, so no reply can carry
    /// two explanations or explain a gap it does not have.
    /// </summary>
    [Theory]
    [InlineData(5, 0, "missing from the default query")]
    [InlineData(5, 3, "missing from the default query")]
    [InlineData(0, 3, "The counts agree even though")]
    [InlineData(0, 0, "none of these users is offline")]
    public void The_agreement_sentence_is_chosen_from_the_counts(int hidden, int offline, string expected)
    {
        string sentence = SampleEcs.Agreement(hidden, offline);

        Assert.Contains(expected, sentence, StringComparison.Ordinal);

        string[] all =
        {
            "missing from the default query",
            "The counts agree even though",
            "none of these users is offline",
        };
        foreach (string other in all)
            if (!string.Equals(other, expected, StringComparison.Ordinal))
                Assert.DoesNotContain(other, sentence, StringComparison.Ordinal);
    }

    [Fact]
    public void The_users_reply_says_the_caller_owns_the_query_from_Build()
    {
        var lines = SampleEcs.UsersLines(1, 1, 1, 0);
        Assert.Contains(lines, l => l.Contains("disposed explicitly", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("Query.Entities is the variant that builds, copies and disposes for you", StringComparison.Ordinal));
    }

    [Fact]
    public void A_resolved_singleton_prints_the_entity_and_the_raw_pointer_verdict()
    {
        var lines = SampleEcs.SingletonLines(
            "ServerRuntimeSettings", found: true, describe: "42:1 [ServerRuntimeSettings]",
            rawPointer: true, zeroSizedTag: false, ecsTypeSize: 0);

        Assert.Contains(lines, l => l.Contains("resolved exactly one entity: 42:1 [ServerRuntimeSettings]", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("TypeManager TypeSize reads 0", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("GetSingleton<T>", StringComparison.Ordinal));
    }

    /// <summary>
    /// R64 finding 5. <c>UserMuteSingleton</c> is an advertised choice whose ordinary outcome is a
    /// refused raw read, so the reply has to name the refusal and its reason. FECS1
    /// (<c>c11968c</c>, evidence 211) made Core answer it from the TypeIndex bit with one warning
    /// instead of letting the engine throw; the sample text is what an operator reads beside that
    /// warning, and it must not present it as a fault.
    /// </summary>
    [Fact]
    public void A_zero_sized_tag_is_reported_as_a_refusal_with_its_reason()
    {
        var lines = SampleEcs.SingletonLines(
            "UserMuteSingleton", found: true, describe: "9:1 [UserMuteSingleton]",
            rawPointer: false, zeroSizedTag: true, ecsTypeSize: 0);

        Assert.Contains(lines, l => l.Contains("resolved exactly one entity", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("refused it: UserMuteSingleton is a zero-sized (tag) component", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("ZeroSizeInChunkTypeFlag (0x40000000)", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("expected answer for a tag", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("TryGetEntity above is the whole story", StringComparison.Ordinal));

        // Not the miss wording, and not "nothing to read" with no reason: both read as a fault.
        Assert.DoesNotContain(lines, l => l.Contains("found no unique entity", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("was not reachable raw", StringComparison.Ordinal));
    }

    /// <summary>
    /// The hex in the reply is the constant Core's gate tests, not a number retyped into prose.
    /// </summary>
    [Fact]
    public void The_refusal_quotes_Cores_own_flag_value()
    {
        Assert.Equal(0x40000000, Progenitor.Ecs.SingletonsZeroSize.ZeroSizeInChunkTypeFlag);
        Assert.True(Progenitor.Ecs.SingletonsZeroSize.IsZeroSized(1073876029));

        string line = SampleEcs.RawRefusedLine("UserMuteSingleton", zeroSizedTag: true);
        Assert.Contains(
            "0x" + Progenitor.Ecs.SingletonsZeroSize.ZeroSizeInChunkTypeFlag.ToString("X", CultureInfo.InvariantCulture),
            line,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// No pointer on a component that is <em>not</em> a tag is a different thing and must not
    /// borrow the refusal's "this is expected" wording.
    /// </summary>
    [Fact]
    public void No_pointer_without_the_tag_flag_is_not_dressed_up_as_the_refusal()
    {
        string line = SampleEcs.RawRefusedLine("ServerRuntimeSettings", zeroSizedTag: false);

        Assert.Contains("is not a zero-sized tag", line, StringComparison.Ordinal);
        Assert.Contains("this is not the refusal path", line, StringComparison.Ordinal);
        Assert.DoesNotContain("expected answer for a tag", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The allowlist keeps the tag on purpose: the two TypeSize-0 names side by side are what
    /// prove TypeSize cannot be the tag test (interop trap 7).
    /// </summary>
    [Fact]
    public void The_allowlist_still_offers_the_tag_component_as_the_refusal_example()
    {
        Assert.Contains("UserMuteSingleton", SampleEcs.KnownSingletons);
        Assert.Contains("ServerRuntimeSettings", SampleEcs.KnownSingletons);

        string src = Source();
        Assert.Contains("zero-sized tag", src, StringComparison.Ordinal);
        Assert.Contains("ZeroSizeInChunkTypeFlag", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The printed TypeSize is the number most likely to be copied into a <c>memcpy</c> by
    /// someone reading this reply, so the line beside it has to say that it is not a length.
    /// </summary>
    [Fact]
    public void A_TypeSize_of_zero_is_printed_as_a_fact_and_disowned_as_a_length()
    {
        string note = SampleEcs.TypeSizeNote(0);

        Assert.Contains("it is not a length", note, StringComparison.Ordinal);
        Assert.Contains("RawWriteGuard.EnsureOverlayMemcpy", note, StringComparison.Ordinal);
        Assert.Contains("never a guessed sizeof", note, StringComparison.Ordinal);
    }

    [Fact]
    public void A_nonzero_TypeSize_is_still_not_a_licence_to_copy_bytes()
    {
        string note = SampleEcs.TypeSizeNote(64);

        Assert.Contains("cross-check", note, StringComparison.Ordinal);
        Assert.Contains("RawWriteGuard still decides", note, StringComparison.Ordinal);
    }

    /// <summary>
    /// A miss is the whole point of the <c>Try</c> shape: the generic it replaces takes the
    /// process down instead of returning false.
    /// </summary>
    [Fact]
    public void A_missing_singleton_is_reported_as_a_false_and_not_as_a_failure()
    {
        var lines = SampleEcs.SingletonLines("UserMuteSingleton", found: false, describe: null, rawPointer: false, zeroSizedTag: true, ecsTypeSize: 0);

        Assert.Contains(lines, l => l.Contains("found no unique entity", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("That is a false, not a crash", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("interop trap 2", StringComparison.Ordinal));
    }

    [Fact]
    public void A_long_component_list_is_cut_rather_than_sent_whole()
    {
        var lines = SampleEcs.SingletonLines(
            "ServerRuntimeSettings", found: true,
            describe: new string('x', SampleEcs.DescribeChars + 100), rawPointer: true,
            zeroSizedTag: false, ecsTypeSize: 0);

        Assert.Contains(lines, l => l.Contains("…", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains(new string('x', SampleEcs.DescribeChars + 1), StringComparison.Ordinal));
    }

    // ---- source pins for the halves that need a live world ----

    /// <summary>
    /// <c>Query.Build</c> hands the caller the query; the example is only an example if the
    /// dispose is there, on both queries, and unconditionally.
    /// </summary>
    [Fact]
    public void Both_queries_are_disposed_explicitly()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "all.Dispose();");
        SourcePin.AssertStatementExactly(src, "onlineOnly.Dispose();");
        SourcePin.AssertStatementExactly(src, "entities.Dispose();");
    }

    /// <summary>
    /// The two flags are the demonstration. Swapping either one silently turns the census into a
    /// pair of identical numbers and the trap-4 lesson disappears.
    /// </summary>
    [Fact]
    public void The_census_builds_one_query_with_disabled_entities_and_one_without()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "var all = Query.Build(em, includeDisabled: true, includeSystems: false, userType);");
        SourcePin.AssertStatementExactly(src, "var onlineOnly = Query.Build(em, includeDisabled: false, includeSystems: false, userType);");
    }

    /// <summary>Interop trap 2: the generic that hard-crashes must not appear in the example.</summary>
    [Fact]
    public void The_singleton_read_never_calls_the_generic_GetSingleton()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "if (!Singletons.TryGetEntity(em, componentType, out var entity))");

        // The call forms, not the phrase: the reply text names the generic in order to warn
        // about it, so a bare "GetSingleton<" search would match the warning itself.
        Assert.DoesNotContain("query.GetSingleton<", src, StringComparison.Ordinal);
        Assert.DoesNotContain("em.GetSingleton<", src, StringComparison.Ordinal);
        Assert.DoesNotContain("all.GetSingleton<", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// The overload that also hands back the TypeSize is the one used, so the reply can print it;
    /// the pointer itself is discarded, because this command reads nothing and writes nothing.
    /// </summary>
    [Fact]
    public void The_raw_read_takes_the_TypeSize_overload_and_discards_the_pointer()
    {
        SourcePin.AssertStatementExactly(
            Source(),
            "rawPointer = Singletons.TryGetRawRW(em, ComponentIndex.IndexOf<T>(), out _, out ecsTypeSize);");
    }

    /// <summary>
    /// The tag verdict in the reply comes from the same bit Core's gate reads, off the TypeIndex
    /// the caller already holds — not from TypeSize, and not from catching the engine's throw.
    /// </summary>
    [Fact]
    public void The_tag_verdict_is_the_TypeIndex_bit_and_never_TypeSize()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(
            src, "zeroSizedTag = SingletonsZeroSize.IsZeroSized(ComponentIndex.IndexOf<T>().Value);");
        SourcePin.AssertNoStatementContaining(src, "ecsTypeSize == 0 ?");
    }

    [Fact]
    public void The_command_is_admin_gated_by_the_attribute()
    {
        SourcePin.AssertStatement(
            Source(),
            "[Command(\"ecs\", adminOnly: true, usage: \".ecs users | .ecs singleton [name] | .ecs tag\",");
    }

    [Theory]
    [InlineData("tag")]
    [InlineData("TAG")]
    [InlineData("  Tag  ")]
    public void The_tag_walk_is_reachable_however_it_is_spelled(string token)
    {
        Assert.True(SampleEcs.TryParse(token, out var kind));
        Assert.Equal(SampleEcs.Kind.Tag, kind);
    }

    [Fact]
    public void The_usage_line_lists_all_three_subcommands()
    {
        Assert.Contains(".ecs users", SampleEcs.UsageReply, StringComparison.Ordinal);
        Assert.Contains(".ecs singleton", SampleEcs.UsageReply, StringComparison.Ordinal);
        Assert.Contains(".ecs tag", SampleEcs.UsageReply, StringComparison.Ordinal);
    }

    [Fact]
    public void A_position_is_printed_with_invariant_decimals()
    {
        Assert.Equal("(1.5, 2.5, 3.5)", SampleEcs.Vec(SampleEcs.TagX, SampleEcs.TagY, SampleEcs.TagZ));
        Assert.Equal("(0, -1.25, 10)", SampleEcs.Vec(0f, -1.25f, 10f));
    }

    /// <summary>
    /// One line per step, each carrying the read-back that proves it: added, written, removed,
    /// destroyed. A reader following the walk should be able to see each call's own answer rather
    /// than one summary at the end.
    /// </summary>
    [Fact]
    public void The_tag_walk_reports_every_step_with_its_read_back()
    {
        var lines = SampleEcs.TagLines(
            "4211:1 []",
            hasAfterAdd: true,
            wrote: "(1.5, 2.5, 3.5)",
            readBack: "(1.5, 2.5, 3.5)",
            hasAfterRemove: false,
            existsAfterDestroy: false);

        Assert.Equal(5, lines.Count);
        Assert.Contains("CreateEntity()", lines[0], StringComparison.Ordinal);
        Assert.Contains("4211:1", lines[0], StringComparison.Ordinal);
        Assert.Contains("Add<" + SampleEcs.TagComponentName + ">(em) -> Has<" + SampleEcs.TagComponentName + ">()=True", lines[1], StringComparison.Ordinal);
        Assert.Contains("value-less overload", lines[1], StringComparison.Ordinal);
        Assert.Contains("wrote (1.5, 2.5, 3.5) -> TryRead gives (1.5, 2.5, 3.5)", lines[2], StringComparison.Ordinal);
        Assert.Contains("Remove<" + SampleEcs.TagComponentName + ">(em) -> Has<" + SampleEcs.TagComponentName + ">()=False", lines[3], StringComparison.Ordinal);
        Assert.Contains("DestroyEntity -> Exists()=False", lines[4], StringComparison.Ordinal);
    }

    /// <summary>
    /// TypeSize is not the write length here either: the typed <c>Write&lt;T&gt;</c> uses
    /// <c>sizeof(T)</c>, and a caller-chosen byte count at TypeSize 0 is what
    /// <c>RawWriteGuard</c> refuses (evidence section 32).
    /// </summary>
    [Fact]
    public void The_write_step_says_the_length_is_sizeof_and_never_TypeSize()
    {
        string line = SampleEcs.TagLines("e", true, "(0, 0, 0)", "(0, 0, 0)", false, false)[2];

        Assert.Contains("sizeof(T)", line, StringComparison.Ordinal);
        Assert.Contains("TypeSize reads 0", line, StringComparison.Ordinal);
        Assert.Contains("RawWriteGuard refuses", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_last_step_says_never_to_run_the_walk_on_a_player()
    {
        string line = SampleEcs.TagLines("e", true, "(0, 0, 0)", "(0, 0, 0)", false, false)[4];

        Assert.Contains("Never do it to a player's character or User entity", line, StringComparison.Ordinal);
    }

    /// <summary>
    /// The target is a throwaway the command makes and destroys in the same frame. The pins are
    /// the create, both structural calls, the destroy, and the <c>finally</c> that runs the
    /// destroy again when the walk threw partway.
    /// </summary>
    [Fact]
    public void The_walk_runs_on_an_entity_the_command_makes_and_always_destroys()
    {
        string src = Source();

        SourcePin.AssertStatementExactly(src, "entity = em.CreateEntity();");
        SourcePin.AssertStatementExactly(src, "entity.Add<Disabled>(em);");
        SourcePin.AssertStatementExactly(src, "entity.Add(em, in value);");
        SourcePin.AssertStatementExactly(src, "entity.Remove<Disabled>(em);");
        SourcePin.AssertStatementExactly(src, "em.DestroyEntity(entity);");
        SourcePin.AssertStatement(src, "if (entity != Entity.Null && em.Exists(entity))");

        // Never a player: no route from the command context's entities into the walk.
        SourcePin.AssertNoStatementContaining(src, "ctx.CharacterEntity");
        SourcePin.AssertNoStatementContaining(src, "ctx.UserEntity");
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleEcs.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleEcs.cs not found from " + AppContext.BaseDirectory);
    }
}
