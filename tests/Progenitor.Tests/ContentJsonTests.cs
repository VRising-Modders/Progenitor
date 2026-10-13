using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content.Json;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// JSON content definition files, phase 1 (roadmap M4.6, job FJSON1).
///
/// <para>The whole point of the split in <c>Content/Json</c> is that everything worth being wrong
/// about is game-free: which files are refused, which item and field the message names, and what
/// actually reaches the builder. These tests drive the production parser and loader directly and
/// fake only the one seam, <see cref="IContentFileSink"/>.</para>
/// </summary>
public class ContentJsonTests
{
    private const string Label = "Progenitor.Sample/content/items.json";

    private static string OneItem(string body) => "{ \"schema\": 1, \"items\": [ " + body + " ] }";

    private const string Ash =
        "{ \"id\": \"sample.ash\", \"cloneOf\": \"Item_Ingredient_Crystal\", "
        + "\"name\": \"Ash Shard\", \"description\": \"Cold ash.\", \"stack\": 50 }";

    /// <summary>Records what the loader asked the registry to do, and can pretend an id exists.</summary>
    private sealed class FakeSink : IContentFileSink
    {
        private readonly HashSet<string> _existing;

        public FakeSink(params string[] existing) =>
            _existing = new HashSet<string>(existing, StringComparer.Ordinal);

        public List<ContentItemSpec> Defined { get; } = new();

        public Func<ContentItemSpec, Exception?>? Throw { get; set; }

        public Exception? IsDefinedThrows { get; set; }

        public bool IsDefined(string id)
        {
            if (IsDefinedThrows != null) throw IsDefinedThrows;
            return _existing.Contains(id);
        }

        public void DefineItem(ContentItemSpec spec)
        {
            var boom = Throw?.Invoke(spec);
            if (boom != null) throw boom;
            Defined.Add(spec);
        }
    }

    private static ContentFileLoadResult Run(FakeSink sink, params (string label, string json)[] files) =>
        ContentFileLoad.Run(files.Select(f => new ContentFileSource(f.label, f.json)).ToList(), sink);

    private static IReadOnlyList<string> Errors(string json)
    {
        Assert.False(ContentFileParser.TryParse(Label, json, out var doc, out var errors));
        Assert.Null(doc);
        Assert.NotEmpty(errors);
        return errors;
    }

    private static string OneError(string json)
    {
        var errors = Errors(json);
        return string.Join(" | ", errors);
    }

    // ---- the happy shape ------------------------------------------------------------------

    [Fact]
    public void A_well_formed_file_parses_into_one_item_with_every_field()
    {
        Assert.True(ContentFileParser.TryParse(Label, OneItem(Ash), out var doc, out var errors));
        Assert.Empty(errors);
        var item = Assert.Single(doc!.Items);
        Assert.Equal(0, item.Index);
        Assert.Equal("sample.ash", item.Id);
        Assert.Equal("Item_Ingredient_Crystal", item.CloneOfName);
        Assert.Null(item.CloneOfGuidHash);
        Assert.Equal("Ash Shard", item.Name);
        Assert.Equal("Cold ash.", item.Description);
        Assert.Equal(50, item.Stack);
        Assert.Equal(ContentFileSchema.Current, doc.Schema);
    }

    [Fact]
    public void Only_id_and_cloneOf_are_required_and_the_rest_stay_null()
    {
        Assert.True(ContentFileParser.TryParse(
            Label, OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\" }"), out var doc, out _));
        var item = Assert.Single(doc!.Items);
        Assert.Null(item.Name);
        Assert.Null(item.Description);
        Assert.Null(item.Stack);
    }

    [Fact]
    public void A_numeric_cloneOf_is_a_guid_hash_and_a_string_is_a_spawnable_name()
    {
        Assert.True(ContentFileParser.TryParse(
            Label, OneItem("{ \"id\": \"a.b\", \"cloneOf\": -257494203 }"), out var doc, out _));
        var item = Assert.Single(doc!.Items);
        Assert.Equal(-257494203, item.CloneOfGuidHash);
        Assert.Null(item.CloneOfName);
    }

    /// <summary>
    /// R67 finding 3. `0` is a valid Int32 and a valid JSON number, so it used to parse; the
    /// builder then sees <c>PrefabGUID.Empty</c>, which is what "CloneOf was never called" looks
    /// like, and throws out of <c>Register</c>. That throw is caught per item by the loader, so
    /// the file would be accepted and every *other* item registered — the half-a-file state the
    /// format's first paragraph forbids. It has to be refused here, at parse time.
    /// </summary>
    [Fact]
    public void A_cloneOf_of_zero_is_refused_at_parse_time_with_the_file_the_index_and_the_field()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 0 }"));
        Assert.Contains(Label, message);
        Assert.Contains("item 0 ('a.b')", message);
        Assert.Contains("field 'cloneOf'", message);
        Assert.Contains("is 0, which is no prefab", message);
        Assert.Contains("spawnable name or a non-zero numeric GUID hash", message);
    }

    /// <summary>
    /// The other half of finding 3: nothing at all reaches the builder from that file, and the
    /// well-formed item beside it does not sneak through.
    /// </summary>
    [Fact]
    public void A_cloneOf_of_zero_refuses_the_whole_file_and_registers_none_of_it()
    {
        var sink = new FakeSink();
        var result = Run(sink, (Label,
            "{ \"schema\": 1, \"items\": [ { \"id\": \"a.a\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"b.b\", \"cloneOf\": 0 } ] }"));

        Assert.Empty(sink.Defined);
        Assert.Equal(0, result.ItemsDefined);
        Assert.Equal(0, result.FilesAccepted);
        Assert.Equal(1, result.FilesRefused);
    }

    [Fact]
    public void Comments_and_trailing_commas_are_tolerated_because_admins_edit_these_by_hand()
    {
        const string json = "{ \"schema\": 1, // the only schema\n \"items\": [ "
            + "{ \"id\": \"a.b\", \"cloneOf\": \"Src\", } ], }";
        Assert.True(ContentFileParser.TryParse(Label, json, out var doc, out var errors));
        Assert.Empty(errors);
        Assert.Single(doc!.Items);
    }

    // ---- refusals: the message names file, item index and field ---------------------------

    [Fact]
    public void An_unknown_item_field_names_the_file_the_item_index_and_the_field()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"ikon\": \"x.png\" }"));
        Assert.Contains(Label, message);
        Assert.Contains("item 0 ('a.b')", message);
        Assert.Contains("unknown field 'ikon'", message);
        Assert.Contains("id, cloneOf, name, description, stack", message);
    }

    [Fact]
    public void The_second_items_index_is_the_one_reported()
    {
        var message = OneError(
            "{ \"schema\": 1, \"items\": [ { \"id\": \"a.b\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"c.d\", \"cloneOf\": \"Src\", \"nope\": 1 } ] }");
        Assert.Contains("item 1 ('c.d')", message);
        Assert.DoesNotContain("item 0", message);
    }

    [Fact]
    public void An_icon_field_on_an_item_says_why_items_have_none()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"icon\": \"x.png\" }"));
        Assert.Contains("unknown field 'icon'", message);
        Assert.Contains("only DefineBuff takes an icon", message);
    }

    [Fact]
    public void An_unknown_root_field_is_refused_and_buffs_says_phase_1_is_items_only()
    {
        var message = OneError("{ \"schema\": 1, \"items\": [], \"buffs\": [] }");
        Assert.Contains(Label, message);
        Assert.Contains("unknown field 'buffs' at the top level", message);
        Assert.Contains("Phase 1 defines items only", message);
    }

    [Fact]
    public void A_wrong_schema_is_refused_and_says_which_one_this_build_reads()
    {
        var message = OneError("{ \"schema\": 2, \"items\": [] }");
        Assert.Contains("schema 2 is not supported", message);
        Assert.Contains("reads schema 1", message);
    }

    [Fact]
    public void A_missing_schema_is_refused()
    {
        Assert.Contains("missing 'schema'", OneError("{ \"items\": [] }"));
    }

    [Fact]
    public void A_missing_items_array_is_refused()
    {
        Assert.Contains("missing 'items'", OneError("{ \"schema\": 1 }"));
    }

    [Fact]
    public void A_top_level_array_is_refused_with_what_it_actually_was()
    {
        Assert.Contains("must be a JSON object, not an array", OneError("[ ]"));
    }

    [Fact]
    public void Broken_json_is_refused_rather_than_thrown()
    {
        Assert.Contains("not valid JSON", OneError("{ \"schema\": 1, "));
    }

    [Fact]
    public void An_empty_file_is_refused()
    {
        Assert.Contains("the file is empty", OneError("   "));
    }

    [Fact]
    public void A_missing_id_is_refused_and_the_line_still_carries_the_index()
    {
        var message = OneError(OneItem("{ \"cloneOf\": \"Src\" }"));
        Assert.Contains("item 0: missing 'id'", message);
    }

    [Fact]
    public void A_missing_cloneOf_is_refused()
    {
        Assert.Contains("missing 'cloneOf'", OneError(OneItem("{ \"id\": \"a.b\" }")));
    }

    [Fact]
    public void A_cloneOf_of_the_wrong_json_type_says_both_forms_it_accepts()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": true }"));
        Assert.Contains("field 'cloneOf'", message);
        Assert.Contains("spawnable name (string) or a GUID hash", message);
        Assert.Contains("not a boolean", message);
    }

    /// <summary>
    /// R68 finding 4. The unsigned spelling of a real hash is the commonest way to write a
    /// `cloneOf` the Int32 arm cannot take — community GUID tables publish them that way — and
    /// the old message told the admin their number "must be … a 32-bit integer, not a number".
    /// The refusal has to name the range and, because a PrefabGUID hash is signed, hand back the
    /// value they meant.
    /// </summary>
    [Fact]
    public void A_cloneOf_outside_the_int32_range_is_refused_as_out_of_range_with_the_signed_hash()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 4037473093 }"));
        Assert.Contains("field 'cloneOf'", message);
        Assert.Contains("is 4037473093", message);
        Assert.Contains("outside the 32-bit range -2147483648 to 2147483647", message);
        Assert.Contains("PrefabGUID hashes are signed 32-bit values", message);
        Assert.Contains("so 4037473093 is written -257494203", message);
        Assert.DoesNotContain("not a number", message);
    }

    /// <summary>
    /// The signed equivalent is only printed when there is one. A value past 2^32 gets the range
    /// and the signedness note without an arithmetic claim nobody can act on.
    /// </summary>
    [Fact]
    public void A_cloneOf_past_the_unsigned_range_gets_the_range_but_no_signed_equivalent()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 99999999999 }"));
        Assert.Contains("is 99999999999", message);
        Assert.Contains("outside the 32-bit range -2147483648 to 2147483647", message);
        Assert.Contains("PrefabGUID hashes are signed 32-bit values.", message);
        Assert.DoesNotContain("is written", message);
    }

    /// <summary>Below the range too, where no unsigned reading exists at all.</summary>
    [Fact]
    public void A_cloneOf_below_the_int32_range_is_refused_as_out_of_range()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": -4037473093 }"));
        Assert.Contains("is -4037473093", message);
        Assert.Contains("outside the 32-bit range", message);
        Assert.DoesNotContain("is written", message);
    }

    /// <summary>
    /// The other reason `TryGetInt32` refuses a number: it is not a whole one. That is a different
    /// edit from an out-of-range value, so it gets a different sentence.
    /// </summary>
    [Fact]
    public void A_fractional_cloneOf_is_refused_as_fractional_not_as_out_of_range()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": -257494203.5 }"));
        Assert.Contains("field 'cloneOf'", message);
        Assert.Contains("is -257494203.5, which is a fractional value", message);
        Assert.Contains("spawnable name or a whole 32-bit GUID hash", message);
        Assert.DoesNotContain("outside the 32-bit range", message);
    }

    /// <summary>
    /// R69 finding 11. `-257494203.0` is the correct hash — it is what `json.dump` and a
    /// spreadsheet export produce — and it is a whole number, so telling the admin it is not one
    /// spends their attention checking something that is already true. What the loader actually
    /// tests is the spelling, so that is what the refusal says, and it hands back the literal to
    /// write.
    /// </summary>
    [Fact]
    public void A_whole_valued_cloneOf_written_with_a_decimal_point_is_refused_for_its_spelling()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": -257494203.0 }"));
        Assert.Contains("is -257494203.0, which is written with a decimal point", message);
        Assert.Contains("write it as -257494203.", message);
        Assert.Contains("spawnable name or an integer literal", message);
        Assert.DoesNotContain("not a whole number", message);
        Assert.DoesNotContain("fractional", message);
    }

    /// <summary>The exponent spelling is the other half of the same finding.</summary>
    [Fact]
    public void A_whole_valued_cloneOf_written_with_an_exponent_is_refused_for_its_spelling()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 1e2 }"));
        Assert.Contains("is 1e2, which is written with an exponent", message);
        Assert.Contains("write it as 100.", message);
        Assert.DoesNotContain("not a whole number", message);
    }

    /// <summary>Both spellings at once are named as both, and the edit is still offered.</summary>
    [Fact]
    public void A_cloneOf_written_with_a_point_and_an_exponent_names_both()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": -2.57494203E8 }"));
        Assert.Contains("which is written with a decimal point and an exponent", message);
        Assert.Contains("write it as -257494203.", message);
    }

    /// <summary>
    /// The suggested integer literal is the value, not the range check: `4037473093.0` is refused
    /// for its spelling and told to write `4037473093`, which the next parse then answers with the
    /// signed-hash sentence. Two true messages in sequence beat one false one.
    /// </summary>
    [Fact]
    public void A_whole_valued_out_of_range_cloneOf_is_refused_for_its_spelling_first()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 4037473093.0 }"));
        Assert.Contains("which is written with a decimal point", message);
        Assert.Contains("write it as 4037473093.", message);
        Assert.DoesNotContain("outside the 32-bit range", message);
    }

    /// <summary>
    /// Past `decimal`'s range there is no spelling worth handing back — a `double`'s exact
    /// expansion is not what was written — so the sentence stands without the example.
    /// </summary>
    [Fact]
    public void A_whole_valued_cloneOf_past_decimal_range_is_refused_without_an_example()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": 1e30 }"));
        Assert.Contains("which is written with an exponent", message);
        Assert.Contains("write it as an integer literal", message);
        Assert.DoesNotContain("1000000000000000019884624838656", message);
    }

    /// <summary>The number rules are the field's, not `cloneOf`'s: `stack` gets both arms too.</summary>
    [Fact]
    public void A_stack_outside_the_int32_range_is_refused_as_out_of_range_without_the_hash_hint()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"stack\": 3000000000 }"));
        Assert.Contains("field 'stack'", message);
        Assert.Contains("is 3000000000, which is outside the 32-bit range -2147483648 to 2147483647.", message);
        Assert.DoesNotContain("PrefabGUID", message);
        Assert.DoesNotContain("not a number", message);
    }

    [Fact]
    public void A_fractional_stack_is_refused_as_fractional()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"stack\": 50.5 }"));
        Assert.Contains("field 'stack'", message);
        Assert.Contains("is 50.5, which is a fractional value; this field takes a whole number.", message);
        Assert.DoesNotContain("PrefabGUID", message);
    }

    /// <summary>`stack` gets the spelling arm too, without the hash lecture.</summary>
    [Fact]
    public void A_whole_valued_stack_written_with_a_decimal_point_is_refused_for_its_spelling()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"stack\": 50.0 }"));
        Assert.Contains("field 'stack'", message);
        Assert.Contains("is 50.0, which is written with a decimal point; this field takes an integer "
                        + "literal, so write it as 50.", message);
        Assert.DoesNotContain("PrefabGUID", message);
        Assert.DoesNotContain("spawnable name", message);
    }

    /// <summary>
    /// A number is unbounded in JSON and the refusal quotes it, so a pasted blob is trimmed rather
    /// than turned into a log line nobody can read. The trim is display only.
    /// </summary>
    [Fact]
    public void A_preposterously_long_number_is_trimmed_in_the_refusal()
    {
        var digits = new string('9', 300);
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": " + digits + " }"));
        Assert.Contains(new string('9', 40) + "...", message);
        Assert.DoesNotContain(new string('9', 41), message);
        Assert.Contains("outside the 32-bit range", message);
    }

    /// <summary>A non-number keeps the both-forms message; the split did not swallow it.</summary>
    [Fact]
    public void A_stack_that_is_not_a_number_still_says_it_must_be_an_integer()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"stack\": \"50\" }"));
        Assert.Contains("field 'stack' must be an integer, not a string.", message);
    }

    [Fact]
    public void A_non_positive_stack_is_refused_with_the_value()
    {
        var message = OneError(OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"stack\": 0 }"));
        Assert.Contains("field 'stack' must be positive; got 0", message);
    }

    [Fact]
    public void A_duplicate_id_inside_one_file_is_refused()
    {
        var message = OneError(
            "{ \"schema\": 1, \"items\": [ { \"id\": \"a.b\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"a.b\", \"cloneOf\": \"Src\" } ] }");
        Assert.Contains("item 1 ('a.b'): id is defined twice in this file", message);
    }

    // ---- the length and ASCII rule, with the byte count ------------------------------------

    [Fact]
    public void A_description_over_the_limit_is_refused_with_its_byte_count()
    {
        var tooLong = new string('x', 64);
        var message = OneError(OneItem(
            "{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"description\": \"" + tooLong + "\" }"));
        Assert.Contains("item 0 ('a.b')", message);
        Assert.Contains("description must be 1", message);
        Assert.Contains("this one is 64 bytes, limit 63", message);
    }

    [Fact]
    public void Exactly_the_limit_is_accepted()
    {
        var atLimit = new string('x', ContentFileLimits.MaxTextBytes);
        Assert.True(ContentFileParser.TryParse(
            Label,
            OneItem("{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"description\": \"" + atLimit + "\" }"),
            out var doc,
            out _));
        Assert.Equal(atLimit, Assert.Single(doc!.Items).Description);
    }

    [Fact]
    public void A_name_over_the_limit_is_refused_the_same_way()
    {
        var message = OneError(OneItem(
            "{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"name\": \"" + new string('n', 70) + "\" }"));
        Assert.Contains("name must be 1", message);
        Assert.Contains("this one is 70 bytes", message);
    }

    /// <summary>
    /// R67 finding 7. The byte count answers "how much do I cut", which is only a question when
    /// the failure is length. A short non-ASCII string got a count too, and the message read like
    /// a length complaint about a three-character name.
    /// </summary>
    [Fact]
    public void Non_ascii_text_is_refused_as_non_ascii_and_carries_no_byte_count()
    {
        var message = OneError(OneItem(
            "{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"name\": \"\\u00e9\\u00e9\\u00e9\" }"));
        Assert.Contains("is not ASCII", message);
        Assert.DoesNotContain("bytes, limit", message);
        Assert.DoesNotContain("this one is", message);
    }

    /// <summary>
    /// The case that actively misled: forty em dashes are forty characters, so the length gate
    /// passes and only the ASCII loop fails — but 120 UTF-8 bytes used to be reported against a
    /// 63-byte limit, telling an admin to cut a string that would still be refused at twenty
    /// characters.
    /// </summary>
    [Fact]
    public void A_long_non_ascii_string_is_not_told_to_get_shorter()
    {
        var dashes = new string('—', 40);
        var message = OneError(OneItem(
            "{ \"id\": \"a.b\", \"cloneOf\": \"Src\", \"name\": \"" + dashes + "\" }"));
        Assert.Contains("is not ASCII", message);
        Assert.DoesNotContain("120 bytes", message);
        Assert.DoesNotContain("limit 63", message);
    }

    /// <summary>
    /// And the discriminator is the one <c>PrefabWire.TryValidateAscii</c> already computes, so a
    /// genuine over-length string still gets its count — including one that is over the limit in
    /// UTF-8 bytes rather than in characters.
    /// </summary>
    [Fact]
    public void An_over_length_id_still_carries_its_byte_count()
    {
        var message = OneError(OneItem(
            "{ \"id\": \"" + new string('i', 64) + "\", \"cloneOf\": \"Src\" }"));
        Assert.Contains("id must be 1", message);
        Assert.Contains("this one is 64 bytes, limit 63", message);
    }

    [Fact]
    public void The_limit_the_file_format_uses_is_the_packet_bound_and_not_a_copy_of_it()
    {
        Assert.Equal(Progenitor.Content.PrefabWire.AsciiMaxChars, ContentFileLimits.MaxTextBytes);
    }

    [Fact]
    public void An_id_that_breaks_the_registry_rule_is_refused_by_the_registry_rule()
    {
        var longId = new string('i', 64);
        var message = OneError(OneItem("{ \"id\": \"" + longId + "\", \"cloneOf\": \"Src\" }"));
        // Same text ValidateId would have thrown, so the file and the C# builder cannot drift.
        Assert.True(Progenitor.Content.PrefabWire.TryValidateAscii(
            longId, "id", out var registryError, out _) == false);
        Assert.Contains(registryError, message);
    }

    // ---- the load: what reaches the builder ------------------------------------------------

    [Fact]
    public void A_good_file_defines_its_items_and_logs_the_count()
    {
        var sink = new FakeSink();
        var result = Run(sink, (Label, OneItem(Ash)));

        Assert.Empty(result.Errors);
        Assert.Equal(1, result.FilesAccepted);
        Assert.Equal(0, result.FilesRefused);
        Assert.Equal(1, result.ItemsDefined);
        Assert.Equal("sample.ash", Assert.Single(sink.Defined).Id);
        Assert.Equal($"Content files: {Label}: 1 item(s) defined", Assert.Single(result.Info));
    }

    [Fact]
    public void One_bad_file_refuses_only_itself()
    {
        var sink = new FakeSink();
        var result = Run(
            sink,
            ("p/content/bad.json", "{ \"schema\": 9, \"items\": [] }"),
            ("p/content/good.json", OneItem(Ash)));

        Assert.Equal(1, result.FilesAccepted);
        Assert.Equal(1, result.FilesRefused);
        Assert.Equal("sample.ash", Assert.Single(sink.Defined).Id);
        Assert.Contains(result.Errors, e => e.Contains("bad.json refused"));
        Assert.DoesNotContain(result.Errors, e => e.Contains("good.json"));
        Assert.Contains(result.Info, i => i.Contains("good.json: 1 item(s) defined"));
    }

    [Fact]
    public void A_refused_file_registers_none_of_its_items_not_even_the_valid_ones()
    {
        var sink = new FakeSink();
        var result = Run(sink, (Label,
            "{ \"schema\": 1, \"items\": [ { \"id\": \"a.b\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"c.d\", \"cloneOf\": \"Src\", \"oops\": 1 } ] }"));

        Assert.Empty(sink.Defined);
        Assert.Equal(0, result.ItemsDefined);
        Assert.Equal(1, result.FilesRefused);
    }

    [Fact]
    public void A_collision_with_a_csharp_registered_id_refuses_the_whole_file()
    {
        var sink = new FakeSink("sample.ember");
        var result = Run(sink, (Label,
            "{ \"schema\": 1, \"items\": [ { \"id\": \"sample.ash\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"sample.ember\", \"cloneOf\": \"Src\" } ] }"));

        Assert.Empty(sink.Defined);
        Assert.Equal(1, result.FilesRefused);
        var joined = string.Join(" | ", result.Errors);
        Assert.Contains("item 1 ('sample.ember')", joined);
        Assert.Contains("already defines that id", joined);
        Assert.Contains("never replaces a definition", joined);
    }

    [Fact]
    public void The_same_id_in_two_files_of_one_load_is_a_collision_too()
    {
        var sink = new FakeSink();
        var result = Run(
            sink,
            ("p/content/a.json", OneItem("{ \"id\": \"x.y\", \"cloneOf\": \"Src\" }")),
            ("p/content/b.json", OneItem("{ \"id\": \"x.y\", \"cloneOf\": \"Src\" }")));

        Assert.Single(sink.Defined);
        Assert.Equal(1, result.FilesRefused);
        Assert.Contains(result.Errors, e => e.Contains("b.json"));
    }

    [Fact]
    public void A_registry_refusal_of_one_item_is_reported_against_that_item_and_the_rest_load()
    {
        var sink = new FakeSink
        {
            Throw = spec => spec.Id == "b.b" ? new InvalidOperationException("source has no ItemData") : null,
        };
        var result = Run(sink, (Label,
            "{ \"schema\": 1, \"items\": [ { \"id\": \"a.a\", \"cloneOf\": \"Src\" }, "
            + "{ \"id\": \"b.b\", \"cloneOf\": \"Src\" }, { \"id\": \"c.c\", \"cloneOf\": \"Src\" } ] }"));

        Assert.Equal(new[] { "a.a", "c.c" }, sink.Defined.Select(s => s.Id).ToArray());
        Assert.Equal(2, result.ItemsDefined);
        var joined = string.Join(" | ", result.Errors);
        Assert.Contains("item 1 ('b.b') was refused by the registry: source has no ItemData", joined);
    }

    [Fact]
    public void A_sink_that_cannot_answer_IsDefined_refuses_the_file_rather_than_guessing()
    {
        var sink = new FakeSink { IsDefinedThrows = new InvalidOperationException("registry locked") };
        var result = Run(sink, (Label, OneItem(Ash)));

        Assert.Empty(sink.Defined);
        Assert.Equal(1, result.FilesRefused);
        Assert.Contains(result.Errors, e => e.Contains("could not be checked against"));
    }

    [Fact]
    public void An_empty_items_array_is_a_valid_file_that_defines_nothing()
    {
        var sink = new FakeSink();
        var result = Run(sink, (Label, "{ \"schema\": 1, \"items\": [] }"));

        Assert.Empty(sink.Defined);
        Assert.Equal(1, result.FilesAccepted);
        Assert.Equal($"Content files: {Label}: 0 item(s) defined", Assert.Single(result.Info));
    }

    [Fact]
    public void Run_rejects_a_null_sink_or_null_file_list()
    {
        Assert.Throws<ArgumentNullException>(() => ContentFileLoad.Run(new List<ContentFileSource>(), null!));
        Assert.Throws<ArgumentNullException>(() => ContentFileLoad.Run(null!, new FakeSink()));
    }

    // ---- the file the Sample actually ships -------------------------------------------------

    [Fact]
    public void The_samples_shipped_items_json_parses_and_defines_sample_ash()
    {
        var json = SourcePin.ReadFile("samples", "Progenitor.Sample", "content", "items.json");
        var sink = new FakeSink("sample.ember", "sample.nodrain");
        var result = Run(sink, ("Progenitor.Sample/content/items.json", json));

        Assert.Empty(result.Errors);
        var item = Assert.Single(sink.Defined);
        Assert.Equal("sample.ash", item.Id);
        Assert.Equal("Item_Ingredient_Crystal", item.CloneOfName);
        Assert.Equal("Ash Shard", item.Name);
        Assert.Equal(50, item.Stack);
        Assert.Equal(
            "Content files: Progenitor.Sample/content/items.json: 1 item(s) defined",
            Assert.Single(result.Info));
    }

    [Fact]
    public void The_sample_csproj_copies_the_content_folder_the_way_it_copies_localization()
    {
        var csproj = SourcePin.ReadFile("samples", "Progenitor.Sample", "Progenitor.Sample.csproj");
        Assert.Contains("<Content Include=\"content\\**\\*.json\">", csproj);
        Assert.Contains("<Link>content\\%(RecursiveDir)%(Filename)%(Extension)</Link>", csproj);
    }

    [Fact]
    public void Deploy_copies_a_plugins_content_folder_into_the_reload_layout()
    {
        var deploy = SourcePin.ReadFile("tools", "deploy.ps1");
        Assert.Contains("Join-Path $dll.DirectoryName \"content\"", deploy);
        Assert.Contains("Invoke-DeployCopyTree $contentSrc $contentDest", deploy);
    }

    [Fact]
    public void Core_scans_a_loaded_plugins_content_folder_after_OnLoad()
    {
        var source = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "ProgenitorPlugin.cs");
        SourcePin.AssertStatement(source, "OnLoad(ctx);");
        SourcePin.AssertStatement(
            source,
            "var content = Progenitor.Content.Json.ContentFiles.LoadForPlugin(GetType().Assembly, this);");
        // After, not before: the collision check only works once the C# ids exist.
        Assert.True(
            source.IndexOf("OnLoad(ctx);", StringComparison.Ordinal)
            < source.IndexOf("ContentFiles.LoadForPlugin", StringComparison.Ordinal),
            "content files must be loaded after OnLoad so a file cannot silently replace a C# id.");
    }

    [Fact]
    public void The_probed_folder_name_is_the_one_the_loader_and_the_schema_agree_on()
    {
        Assert.Equal("content", ContentFileSchema.FolderName);
    }
}
