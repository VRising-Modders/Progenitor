using System.Collections.Generic;
using Progenitor.Localization;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// LocalizationParser is the game-free localization surface (JSON shape, ISO-to-Steam names,
/// and the folder probe used by LoadForPlugin). File I/O and Register live in Localization
/// (not linked here).
/// </summary>
public class LocalizationParserTests
{
    [Fact]
    public void TryParse_reads_a_flat_object_of_strings()
    {
        Assert.True(LocalizationParser.TryParse(
            """{"sample.ember":"Ember Shard","sample.ember.desc":"A shard that still holds a little heat."}""",
            out var entries,
            out var error));
        Assert.Null(error);
        Assert.Equal("Ember Shard", entries["sample.ember"]);
        Assert.Equal("A shard that still holds a little heat.", entries["sample.ember.desc"]);
        Assert.Equal(2, entries.Count);
    }

    [Fact]
    public void TryParse_empty_object_is_valid_with_no_keys()
    {
        Assert.True(LocalizationParser.TryParse("{}", out var entries, out var error));
        Assert.Null(error);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void TryParse_empty_input_fails(string? json)
    {
        Assert.False(LocalizationParser.TryParse(json, out var entries, out var error));
        Assert.NotNull(error);
        Assert.Empty(entries);
    }

    [Theory]
    [InlineData("{")]
    [InlineData("[1,2]")]
    [InlineData("\"just a string\"")]
    [InlineData("12")]
    [InlineData("{\"a\":\"b\"")]
    public void TryParse_malformed_or_non_object_root_fails(string json)
    {
        Assert.False(LocalizationParser.TryParse(json, out var entries, out var error));
        Assert.False(string.IsNullOrEmpty(error));
        Assert.Empty(entries);
    }

    [Fact]
    public void TryParse_skips_non_string_values_and_keeps_the_rest()
    {
        Assert.True(LocalizationParser.TryParse(
            """{"keep":"yes","num":1,"flag":true,"obj":{"nested":"no"},"arr":["x"]}""",
            out var entries,
            out var error));
        Assert.Null(error);
        Assert.Equal(new Dictionary<string, string> { ["keep"] = "yes" }, entries);
    }

    [Fact]
    public void TryParse_null_value_becomes_empty_string()
    {
        Assert.True(LocalizationParser.TryParse("""{"blank":null}""", out var entries, out _));
        Assert.Equal("", entries["blank"]);
    }

    [Fact]
    public void TryParse_last_duplicate_key_wins()
    {
        Assert.True(LocalizationParser.TryParse("""{"a":"first","a":"second"}""", out var entries, out _));
        Assert.Equal("second", entries["a"]);
        Assert.Single(entries);
    }

    [Fact]
    public void TryParse_skips_empty_keys()
    {
        Assert.True(LocalizationParser.TryParse("""{"":"nope","  ":"nope","ok":"yes"}""", out var entries, out _));
        Assert.Equal(new Dictionary<string, string> { ["ok"] = "yes" }, entries);
    }

    [Fact]
    public void TryParse_preserves_unicode_and_escapes()
    {
        Assert.True(LocalizationParser.TryParse(
            """{"ja":"日本語","quote":"say \"hi\"","nl":"a\nb"}""",
            out var entries,
            out _));
        Assert.Equal("日本語", entries["ja"]);
        Assert.Equal("say \"hi\"", entries["quote"]);
        Assert.Equal("a\nb", entries["nl"]);
    }

    [Fact]
    public void TryParse_allows_comments_and_trailing_commas()
    {
        const string json = """
            {
              // translator note
              "sample.ember": "Ember Shard",
            }
            """;
        Assert.True(LocalizationParser.TryParse(json, out var entries, out var error));
        Assert.Null(error);
        Assert.Equal("Ember Shard", entries["sample.ember"]);
    }

    [Fact]
    public void TryParse_keys_are_case_sensitive()
    {
        Assert.True(LocalizationParser.TryParse(
            """{"Sample.ember":"A","sample.ember":"B"}""",
            out var entries,
            out _));
        Assert.Equal(2, entries.Count);
        Assert.Equal("A", entries["Sample.ember"]);
        Assert.Equal("B", entries["sample.ember"]);
    }

    [Theory]
    [InlineData("en.json", "en")]
    [InlineData("de.json", "de")]
    [InlineData("EN.JSON", "EN")]
    [InlineData("English.json", "English")]
    [InlineData(@"C:\mods\localization\de.json", "de")]
    [InlineData("C:/mods/localization/fr.json", "fr")]
    [InlineData("en-US.json", "en-US")]
    [InlineData("  zh-TW.json  ", "zh-TW")]
    public void LanguageFromFileName_uses_the_stem(string path, string expected)
    {
        Assert.Equal(expected, LocalizationParser.LanguageFromFileName(path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void LanguageFromFileName_empty_is_empty(string? path)
    {
        Assert.Equal("", LocalizationParser.LanguageFromFileName(path));
    }

    [Theory]
    [InlineData("en")]
    [InlineData("EN")]
    [InlineData("en-US")]
    [InlineData("en_GB")]
    [InlineData("eng")]
    [InlineData("English")]
    [InlineData("english")]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void CanonicalLanguage_maps_english_spellings_to_English(string? input)
    {
        Assert.Equal(LocalizationParser.FallbackLanguage, LocalizationParser.CanonicalLanguage(input));
        Assert.True(LocalizationParser.IsFallbackLanguage(input));
    }

    [Theory]
    [InlineData("de", "German")]
    [InlineData("DE", "German")]
    [InlineData("de-DE", "German")]
    [InlineData("German", "German")]
    [InlineData("fr", "French")]
    [InlineData("it", "Italian")]
    [InlineData("es", "Spanish")]
    [InlineData("es-MX", "Latam")]
    [InlineData("pl", "Polish")]
    [InlineData("ru", "Russian")]
    [InlineData("ja", "Japanese")]
    [InlineData("ko", "Koreana")]
    [InlineData("Koreana", "Koreana")]
    [InlineData("zh", "SChinese")]
    [InlineData("zh-CN", "SChinese")]
    [InlineData("zh-TW", "TChinese")]
    [InlineData("zh-Hant", "TChinese")]
    [InlineData("pt-BR", "Brazilian")]
    [InlineData("pt", "Brazilian")]
    [InlineData("hu", "Hungarian")]
    [InlineData("tr", "Turkish")]
    [InlineData("th", "Thai")]
    [InlineData("uk", "Ukrainian")]
    [InlineData("vi", "Vietnamese")]
    [InlineData("ca", "Catalan")]
    public void CanonicalLanguage_maps_iso_codes_to_game_names(string input, string expected)
    {
        Assert.Equal(expected, LocalizationParser.CanonicalLanguage(input));
    }

    [Fact]
    public void CanonicalLanguage_is_idempotent_on_game_names()
    {
        Assert.Equal("English", LocalizationParser.CanonicalLanguage(LocalizationParser.CanonicalLanguage("en")));
        Assert.Equal("German", LocalizationParser.CanonicalLanguage(LocalizationParser.CanonicalLanguage("de")));
        Assert.Equal("Koreana", LocalizationParser.CanonicalLanguage("Koreana"));
        Assert.Equal("SChinese", LocalizationParser.CanonicalLanguage("SChinese"));
    }

    [Fact]
    public void CanonicalLanguage_preserves_unknown_tokens()
    {
        Assert.Equal("elvish", LocalizationParser.CanonicalLanguage("elvish"));
        Assert.Equal("Elvish", LocalizationParser.CanonicalLanguage("Elvish"));
        Assert.False(LocalizationParser.IsFallbackLanguage("elvish"));
    }

    [Fact]
    public void IsFallbackLanguage_is_false_for_other_game_languages()
    {
        Assert.False(LocalizationParser.IsFallbackLanguage("de"));
        Assert.False(LocalizationParser.IsFallbackLanguage("German"));
        Assert.False(LocalizationParser.IsFallbackLanguage("fr"));
    }

    [Fact]
    public void File_stem_en_resolves_to_the_english_fallback()
    {
        var stem = LocalizationParser.LanguageFromFileName("en.json");
        Assert.Equal("en", stem);
        Assert.Equal(LocalizationParser.FallbackCode, stem);
        Assert.True(LocalizationParser.IsFallbackLanguage(stem));
        Assert.Equal("English", LocalizationParser.CanonicalLanguage(stem));
    }
}
