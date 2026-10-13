using System;
using Progenitor.Commands;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// CommandParser is game-free. Remainder *binding* lives in CommandDispatcher (not linked here);
/// these tests cover the token spans the dispatcher uses to slice a remainder from the original
/// string. Registry discovery is covered in <see cref="CommandRegistryTests"/>.
/// </summary>
public class CommandParserTests
{
    [Fact]
    public void SelfCheck_passes()
    {
        Assert.True(CommandParser.RunSelfCheck(out var report), report);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n\r")]
    public void Tokenize_empty_or_whitespace_yields_no_tokens(string? input)
    {
        Assert.Empty(CommandParser.Tokenize(input));
    }

    [Fact]
    public void Tokenize_splits_on_any_whitespace()
    {
        AssertEqualTokens("a b c", "a", "b", "c");
        AssertEqualTokens("  a   b  ", "a", "b");
        AssertEqualTokens("one\ttwo", "one", "two");
        AssertEqualTokens("one\ntwo\rthree", "one", "two", "three");
    }

    [Fact]
    public void Tokenize_quoted_span_keeps_spaces_and_drops_quotes()
    {
        AssertEqualTokens("\"hello world\"", "hello world");
        AssertEqualTokens("cmd \"hello world\" x", "cmd", "hello world", "x");
    }

    [Fact]
    public void Tokenize_empty_quoted_span_is_an_empty_string_token()
    {
        AssertEqualTokens("\"\"", "");
        AssertEqualTokens("yes \"\" no", "yes", "", "no");
    }

    [Fact]
    public void Tokenize_unclosed_quote_takes_the_remainder()
    {
        AssertEqualTokens("\"unclosed", "unclosed");
        AssertEqualTokens("cmd \"still open", "cmd", "still open");
    }

    [Fact]
    public void Tokenize_backslash_escapes_only_inside_quotes()
    {
        AssertEqualTokens("\"a \\\"b\\\" c\"", "a \"b\" c");
        AssertEqualTokens("\"\\\\\"", "\\");
        AssertEqualTokens("\"\\n\"", "n");
        AssertEqualTokens("a\\ b", "a\\", "b");
        AssertEqualTokens("a\\b", "a\\b");
    }

    [Fact]
    public void Tokenize_trailing_backslash_in_quoted_span_is_kept()
    {
        AssertEqualTokens("\"abc\\", "abc\\");
    }

    [Fact]
    public void Tokenize_quote_in_the_middle_of_an_unquoted_token_is_literal()
    {
        AssertEqualTokens("say\"hello\"", "say\"hello\"");
        AssertEqualTokens("a\"b c", "a\"b", "c");
    }

    [Fact]
    public void Tokenize_unicode_letters_and_emoji_stay_in_the_token()
    {
        AssertEqualTokens("café naïve", "café", "naïve");
        AssertEqualTokens("\"日本語 テスト\"", "日本語 テスト");
        AssertEqualTokens("drop 🎮 here", "drop", "🎮", "here");
        AssertEqualTokens("имя \"значение с пробелом\"", "имя", "значение с пробелом");
    }

    [Fact]
    public void Tokenize_unicode_whitespace_separates_tokens()
    {
        AssertEqualTokens("a\u00A0b", "a", "b");
        AssertEqualTokens("a\u3000b", "a", "b");
    }

    [Fact]
    public void TokenizeDetailed_spans_cover_the_original_lexeme_including_quotes()
    {
        const string input = "say \"hello there\" extra";
        var tokens = CommandParser.TokenizeDetailed(input);
        Assert.Equal(3, tokens.Count);
        Assert.Equal("say", tokens[0].Value);
        Assert.Equal("hello there", tokens[1].Value);
        Assert.Equal("extra", tokens[2].Value);

        Assert.Equal("say", Slice(input, tokens[0]));
        Assert.Equal("\"hello there\"", Slice(input, tokens[1]));
        Assert.Equal("extra", Slice(input, tokens[2]));
    }

    [Fact]
    public void Remainder_after_consumed_tokens_is_verbatim_from_the_original_string()
    {
        const string input = "say \"hello there\" extra";
        Assert.Equal("say \"hello there\" extra", RemainderAfter(input, consumed: 0));
        Assert.Equal("\"hello there\" extra", RemainderAfter(input, consumed: 1));
        Assert.Equal("extra", RemainderAfter(input, consumed: 2));
        Assert.Equal("", RemainderAfter(input, consumed: 3));
    }

    [Fact]
    public void Remainder_preserves_quotes_and_spacing_the_tokenizer_stripped()
    {
        const string input = "tell  \"a  b\"   c";
        Assert.Equal("\"a  b\"   c", RemainderAfter(input, consumed: 1));
    }

    [Fact]
    public void Remainder_of_a_single_token_line_is_empty()
    {
        Assert.Equal("", RemainderAfter("only", consumed: 1));
        Assert.Equal("", RemainderAfter("", consumed: 0));
    }

    [Theory]
    [InlineData("42", 42)]
    [InlineData("-7", -7)]
    [InlineData("0", 0)]
    [InlineData("2147483647", int.MaxValue)]
    [InlineData(" 8 ", 8)]
    public void TryConvert_int_accepts_invariant_integers(string token, int expected)
    {
        Assert.True(CommandParser.TryConvert(token, typeof(int), out var value, out var spec));
        Assert.Equal(expected, value);
        Assert.Equal(40, spec);
    }

    [Theory]
    [InlineData("hello")]
    [InlineData("3.2")]
    [InlineData("1e2")]
    [InlineData("2147483648")]
    [InlineData("")]
    [InlineData("0x10")]
    public void TryConvert_int_rejects_invalid_tokens(string token)
    {
        Assert.False(CommandParser.TryConvert(token, typeof(int), out var value, out var spec));
        Assert.Null(value);
        Assert.Equal(0, spec);
    }

    [Fact]
    public void TryConvert_long_accepts_values_outside_int_range()
    {
        Assert.True(CommandParser.TryConvert("2147483648", typeof(long), out var value, out var spec));
        Assert.Equal(2147483648L, value);
        Assert.Equal(30, spec);
    }

    [Theory]
    [InlineData("-3.5", -3.5f)]
    [InlineData("1e2", 100f)]
    [InlineData("0", 0f)]
    public void TryConvert_float_uses_invariant_culture(string token, float expected)
    {
        Assert.True(CommandParser.TryConvert(token, typeof(float), out var value, out _));
        Assert.Equal(expected, Assert.IsType<float>(value));
    }

    [Fact]
    public void TryConvert_float_rejects_comma_decimals()
    {
        Assert.False(CommandParser.TryConvert("3,5", typeof(float), out _, out _));
    }

    [Fact]
    public void TryConvert_double_parses_scientific_notation()
    {
        Assert.True(CommandParser.TryConvert("1e2", typeof(double), out var value, out var spec));
        Assert.Equal(100.0, value);
        Assert.Equal(15, spec);
    }

    [Theory]
    [InlineData("true", true, 50)]
    [InlineData("TRUE", true, 50)]
    [InlineData("yes", true, 50)]
    [InlineData("on", true, 50)]
    [InlineData("1", true, 10)]
    [InlineData("false", false, 50)]
    [InlineData("No", false, 50)]
    [InlineData("off", false, 50)]
    [InlineData("0", false, 10)]
    public void TryConvert_bool_accepts_named_and_digit_tokens(string token, bool expected, int specificity)
    {
        Assert.True(CommandParser.TryConvert(token, typeof(bool), out var value, out var spec));
        Assert.Equal(expected, value);
        Assert.Equal(specificity, spec);
    }

    [Theory]
    [InlineData("maybe")]
    [InlineData("2")]
    [InlineData("")]
    [InlineData("trueish")]
    public void TryConvert_bool_rejects_unknown_tokens(string token)
    {
        Assert.False(CommandParser.TryConvert(token, typeof(bool), out var value, out _));
        Assert.Null(value);
    }

    [Fact]
    public void TryConvert_string_always_succeeds_including_empty()
    {
        Assert.True(CommandParser.TryConvert("", typeof(string), out var empty, out var emptySpec));
        Assert.Equal("", empty);
        Assert.Equal(1, emptySpec);

        Assert.True(CommandParser.TryConvert("hello", typeof(string), out var hello, out _));
        Assert.Equal("hello", hello);
    }

    [Fact]
    public void TryConvert_int_out_specifies_string_so_a_digit_prefers_int()
    {
        Assert.True(CommandParser.TryConvert("5", typeof(int), out _, out int intSpec));
        Assert.True(CommandParser.TryConvert("5", typeof(string), out _, out int strSpec));
        Assert.True(intSpec > strSpec);
    }

    [Fact]
    public void TryConvert_unsupported_type_fails()
    {
        Assert.False(CommandParser.TryConvert("1", typeof(decimal), out var value, out var spec));
        Assert.Null(value);
        Assert.Equal(0, spec);
        Assert.False(CommandParser.IsSupportedType(typeof(decimal)));
        Assert.False(CommandParser.IsSupportedType(typeof(DateTime)));
    }

    [Fact]
    public void IsSupportedType_covers_the_converter_set()
    {
        Assert.True(CommandParser.IsSupportedType(typeof(string)));
        Assert.True(CommandParser.IsSupportedType(typeof(int)));
        Assert.True(CommandParser.IsSupportedType(typeof(long)));
        Assert.True(CommandParser.IsSupportedType(typeof(float)));
        Assert.True(CommandParser.IsSupportedType(typeof(double)));
        Assert.True(CommandParser.IsSupportedType(typeof(bool)));
    }

    private static void AssertEqualTokens(string input, params string[] expected)
    {
        var got = CommandParser.Tokenize(input);
        Assert.Equal(expected, got);
    }

    private static string Slice(string input, CommandParser.ArgToken token) =>
        input.Substring(token.Start, token.End - token.Start);

    /// <summary>
    /// Mirrors how CommandDispatcher slices a [Remainder] argument: from the start of the first
    /// unconsumed token through the end of the original string, quotes and spacing kept.
    /// </summary>
    private static string RemainderAfter(string input, int consumed)
    {
        var tokens = CommandParser.TokenizeDetailed(input);
        if (tokens.Count == 0 || consumed >= tokens.Count) return "";
        return input.Substring(tokens[consumed].Start);
    }
}
