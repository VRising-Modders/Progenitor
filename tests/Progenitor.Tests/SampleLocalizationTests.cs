using System;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX3 (RD6 J10). The reply half of the programmatic-localization example.
///
/// <b>What this can and cannot reach.</b> <c>Localization.Register</c> binds ProjectM and
/// Stunlock types, so the calls themselves are behind <c>!PROGENITOR_TESTS</c> and are checked in
/// the game (the server's <c>Sample loc: registered …</c> line, and the client's
/// <c>Sample loc: client table …</c> read-back). What is game-free — and what a reader of the
/// reply actually depends on — is that the two routes are told apart, that a missing game string
/// is named rather than printed as an empty pair of quotes, and that the server says why its
/// game-table reads are false instead of looking broken.
/// </summary>
public class SampleLocalizationTests
{
    [Fact]
    public void The_runtime_key_is_not_one_of_the_file_keys()
    {
        // A key that also lives in localization/en.json would demonstrate nothing about the
        // programmatic path: the file loader would have registered it either way.
        Assert.NotEqual(SampleLocalization.FileKey, SampleLocalization.RuntimeKey);
        Assert.StartsWith("sample.loc.", SampleLocalization.RuntimeKey, StringComparison.Ordinal);
    }

    [Fact]
    public void A_missing_game_string_is_named_rather_than_printed_as_empty_quotes()
    {
        Assert.Equal(SampleLocalization.NoGameString, SampleLocalization.Quote(null));
        Assert.Equal(SampleLocalization.NoGameString, SampleLocalization.Quote(""));
        Assert.Equal("\"Ember Shard\"", SampleLocalization.Quote("Ember Shard"));
    }

    [Fact]
    public void The_register_line_says_which_side_wrote_the_game_table()
    {
        string server = SampleLocalization.RegisteredLine(isServer: true);
        string client = SampleLocalization.RegisteredLine(isServer: false);

        Assert.Contains(SampleLocalization.RuntimeKey, server, StringComparison.Ordinal);
        Assert.Contains(SampleLocalization.RuntimeText, server, StringComparison.Ordinal);
        Assert.Contains("no game string table here", server, StringComparison.Ordinal);
        Assert.Contains("written into the game's table", client, StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_read_back_line_is_greppable_and_carries_the_value()
    {
        Assert.Equal("client table: '" + SampleLocalization.RuntimeKey + "' -> \"hello\"",
            SampleLocalization.ClientTableLine("hello"));
        Assert.Contains(SampleLocalization.NoGameString, SampleLocalization.ClientTableLine(null), StringComparison.Ordinal);
    }

    [Fact]
    public void The_server_reply_shows_both_routes_and_explains_its_own_empty_reads()
    {
        // What .loc actually prints on the test server: both keys registered, neither in a game
        // table, because there is no game table on a server.
        var lines = SampleLocalization.Lines(isServer: true, runtimeRegistered: true, runtimeGameText: null, fileRegistered: true, fileGameText: null);

        Assert.Equal(3, lines.Count);
        Assert.Contains("IsRegistered=True", lines[0], StringComparison.Ordinal);
        Assert.Contains(SampleLocalization.NoGameString, lines[0], StringComparison.Ordinal);
        Assert.Contains("localization/en.json", lines[1], StringComparison.Ordinal);
        Assert.Contains("This is the server", lines[2], StringComparison.Ordinal);
        Assert.Contains("Sample loc: client table", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void The_client_reply_says_the_quoted_values_are_the_real_ones()
    {
        var lines = SampleLocalization.Lines(isServer: false, runtimeRegistered: true, runtimeGameText: SampleLocalization.RuntimeText, fileRegistered: true, fileGameText: "Ember Shard");

        Assert.Contains("\"" + SampleLocalization.RuntimeText + "\"", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"Ember Shard\"", lines[1], StringComparison.Ordinal);
        Assert.Contains("This is the client", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void An_unregistered_key_reads_False_rather_than_being_omitted()
    {
        var lines = SampleLocalization.Lines(isServer: true, runtimeRegistered: false, runtimeGameText: null, fileRegistered: true, fileGameText: null);

        Assert.Contains("IsRegistered=False", lines[0], StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("sample.loc.demo", "sample.loc.demo")]
    [InlineData("  sample.loc.demo  ", "sample.loc.demo")]
    [InlineData("a", "a")]
    public void A_printable_ascii_key_is_accepted_and_trimmed(string given, string expected)
    {
        Assert.True(SampleLocalization.TryParseId(given, out string clean, out string error));
        Assert.Equal(expected, clean);
        Assert.Equal("", error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    public void An_empty_key_is_refused_with_the_usage_line(string? given)
    {
        Assert.False(SampleLocalization.TryParseId(given, out string clean, out string error));
        Assert.Equal("", clean);
        Assert.Contains(SampleLocalization.UsageReply, error, StringComparison.Ordinal);
    }

    [Fact]
    public void A_key_with_a_space_or_a_control_character_is_refused_and_the_position_is_named()
    {
        Assert.False(SampleLocalization.TryParseId("sample loc", out _, out string spaced));
        Assert.Contains("position 7", spaced, StringComparison.Ordinal);

        Assert.False(SampleLocalization.TryParseId("sample\tloc", out _, out string tabbed));
        Assert.Contains("printable ASCII", tabbed, StringComparison.Ordinal);

        // Non-ASCII is refused too: the key is hashed as UTF-8 bytes, and a key nobody can type
        // twice the same way is a key nothing will ever match.
        Assert.False(SampleLocalization.TryParseId("sample.é", out _, out _));
    }

    [Fact]
    public void An_over_long_key_is_refused_and_the_limit_is_named()
    {
        Assert.False(SampleLocalization.TryParseId(new string('k', SampleLocalization.MaxIdLength + 1), out _, out string error));
        Assert.Contains(SampleLocalization.MaxIdLength.ToString(), error, StringComparison.Ordinal);
        Assert.True(SampleLocalization.TryParseId(new string('k', SampleLocalization.MaxIdLength), out _, out _));
    }

    [Fact]
    public void The_apply_text_is_trimmed_capped_and_never_empty()
    {
        Assert.True(SampleLocalization.TryParseText("  Ember Shard  ", out string clean, out _));
        Assert.Equal("Ember Shard", clean);

        Assert.False(SampleLocalization.TryParseText("   ", out _, out string empty));
        Assert.Contains(SampleLocalization.UsageReply, empty, StringComparison.Ordinal);

        Assert.False(SampleLocalization.TryParseText(new string('x', SampleLocalization.MaxTextLength + 1), out _, out string over));
        Assert.Contains(SampleLocalization.MaxTextLength.ToString(), over, StringComparison.Ordinal);
    }

    [Fact]
    public void Loc_key_prints_the_derived_guid_the_record_and_the_game_table()
    {
        var lines = SampleLocalization.KeyLines("sample.ember", unchecked((int)0xDEADBEEF), registered: true, isServer: true, gameText: null);

        Assert.Equal(3, lines.Count);
        Assert.Contains("KeyFor(\"sample.ember\")", lines[0], StringComparison.Ordinal);
        Assert.Contains("DEADBEEF", lines[0], StringComparison.Ordinal);
        Assert.Contains("MD5", lines[0], StringComparison.Ordinal);
        Assert.Contains("IsRegistered(\"sample.ember\") = True", lines[1], StringComparison.Ordinal);
        Assert.Contains(SampleLocalization.NoGameString, lines[2], StringComparison.Ordinal);
        Assert.Contains("server: always false", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Loc_key_on_a_client_quotes_what_ui_would_resolve()
    {
        var lines = SampleLocalization.KeyLines("sample.ember", 1, registered: false, isServer: false, gameText: "Ember Shard");

        Assert.Contains("IsRegistered(\"sample.ember\") = False", lines[1], StringComparison.Ordinal);
        Assert.Contains("\"Ember Shard\"", lines[2], StringComparison.Ordinal);
        Assert.Contains("client: this is what UI would resolve", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Loc_apply_reports_the_two_keys_it_registered()
    {
        var lines = SampleLocalization.ApplyLines("sample.loc.demo", "Warm Ember", "sample.ember", isServer: true, managedFound: false);

        Assert.Contains("'sample.loc.demo'", lines[0], StringComparison.Ordinal);
        Assert.Contains("'sample.loc.demo" + SampleLocalization.DescriptionSuffix + "'", lines[0], StringComparison.Ordinal);
        Assert.Contains("\"Warm Ember\"", lines[0], StringComparison.Ordinal);
    }

    [Fact]
    public void Loc_apply_on_a_server_says_the_managed_registry_is_a_client_layer()
    {
        var lines = SampleLocalization.ApplyLines("sample.loc.demo", "Warm Ember", "sample.ember", isServer: true, managedFound: false);

        Assert.Equal(3, lines.Count);
        Assert.Contains("No ManagedItemData for sample.ember", lines[1], StringComparison.Ordinal);
        Assert.Contains("client-side layer", lines[2], StringComparison.Ordinal);
        // The half a server can do is not reported as a failure.
        Assert.Contains("the half that matters", lines[2], StringComparison.Ordinal);
    }

    [Fact]
    public void Loc_apply_that_found_managed_data_names_both_calls()
    {
        var lines = SampleLocalization.ApplyLines("sample.loc.demo", "Warm Ember", "sample.ember", isServer: false, managedFound: true);

        Assert.Equal(3, lines.Count);
        Assert.Contains("ApplyName(data, \"sample.loc.demo\")", lines[1], StringComparison.Ordinal);
        Assert.Contains("ApplyDescription(data, \"sample.loc.demo.desc\")", lines[1], StringComparison.Ordinal);
        Assert.Contains("LocalizationKey", lines[2], StringComparison.Ordinal);
    }

    /// <summary>
    /// The whole reason the two subcommands are a group: the dispatcher checks the group table
    /// before the root table, so a second registration of the bare name <c>loc</c> — which would
    /// collide with <c>SampleCommands</c>'s — is never made.
    /// </summary>
    [Fact]
    public void The_argument_taking_subcommands_are_a_CommandGroup_and_not_a_second_loc_command()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "[CommandGroup(\"loc\")]");
        SourcePin.AssertStatementExactly(src, "ctx.Commands.Register(typeof(SampleLocalizationCommands));");
        SourcePin.AssertNoStatementContaining(src, "[Command(\"loc\"");
    }

    [Fact]
    public void Apply_points_the_managed_item_data_with_both_localization_calls()
    {
        string src = Source();
        SourcePin.AssertStatementExactly(src, "LocalizationApi.ApplyName(data, id);");
        SourcePin.AssertStatementExactly(src, "LocalizationApi.ApplyDescription(data, id + DescriptionSuffix);");
        // TryGet takes the GUID as an int and builds the key natively; the interop dictionary's
        // own ContainsKey lies about a ValueType-derived key (interop trap 3).
        SourcePin.AssertStatement(src, "if (!registry.TryGet<ManagedItemData>(Plugin.EmberGuid, out var data) || data == null)");
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleLocalization.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleLocalization.cs not found from " + AppContext.BaseDirectory);
    }
}
