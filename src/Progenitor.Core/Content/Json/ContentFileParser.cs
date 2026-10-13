using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Progenitor.Content.Json;

/// <summary>
/// Reads one JSON content file into a <see cref="ContentFileDocument"/>, or into a list of
/// refusals. Game-free.
///
/// <para><b>All-or-nothing per file.</b> Every problem in the file is collected and the file is
/// then refused whole; nothing from a file with an error is ever registered. Half a file is the
/// worst outcome available here — an admin who mistypes the fourth item would otherwise get the
/// first three live and no obvious way to tell which state the server is in — and a file error
/// stops at that file, so the other files in the folder still load.</para>
///
/// <para>Every message names the file, the item index and the field, in that order, because that
/// is the whole of what an admin needs to find the character to change.</para>
/// </summary>
internal static class ContentFileParser
{
    private static readonly JsonDocumentOptions Options = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    /// <summary>
    /// Parse and validate. Returns true with a document and no errors, or false with at least
    /// one error and no document.
    /// </summary>
    public static bool TryParse(
        string label,
        string? json,
        out ContentFileDocument? document,
        out IReadOnlyList<string> errors)
    {
        if (label == null) throw new ArgumentNullException(nameof(label));
        document = null;
        var problems = new List<string>();

        if (string.IsNullOrWhiteSpace(json))
        {
            problems.Add($"{label}: the file is empty.");
            errors = problems;
            return false;
        }

        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json!, Options);
        }
        catch (JsonException e)
        {
            problems.Add($"{label}: not valid JSON: {e.Message}");
            errors = problems;
            return false;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                problems.Add($"{label}: the top level must be a JSON object, not {Describe(root.ValueKind)}.");
                errors = problems;
                return false;
            }

            JsonElement schemaElement = default;
            JsonElement itemsElement = default;
            bool hasSchema = false;
            bool hasItems = false;

            foreach (var property in root.EnumerateObject())
            {
                switch (property.Name)
                {
                    case "schema":
                        schemaElement = property.Value;
                        hasSchema = true;
                        break;
                    case "items":
                        itemsElement = property.Value;
                        hasItems = true;
                        break;
                    default:
                        problems.Add(
                            $"{label}: unknown field '{property.Name}' at the top level; "
                            + $"schema {ContentFileSchema.Current} knows {Join(ContentFileSchema.RootFields)}."
                            + Hint(property.Name));
                        break;
                }
            }

            if (!hasSchema)
            {
                problems.Add($"{label}: missing 'schema'; this build reads schema {ContentFileSchema.Current}.");
            }
            else if (schemaElement.ValueKind != JsonValueKind.Number
                     || !schemaElement.TryGetInt32(out int schema))
            {
                problems.Add(
                    $"{label}: field 'schema' must be the number {ContentFileSchema.Current}, "
                    + $"not {Describe(schemaElement.ValueKind)}.");
            }
            else if (schema != ContentFileSchema.Current)
            {
                problems.Add(
                    $"{label}: schema {schema} is not supported; this build reads schema "
                    + $"{ContentFileSchema.Current}.");
            }

            var items = new List<ContentItemSpec>();
            if (!hasItems)
            {
                problems.Add($"{label}: missing 'items'; phase 1 files define items.");
            }
            else if (itemsElement.ValueKind != JsonValueKind.Array)
            {
                problems.Add($"{label}: field 'items' must be an array, not {Describe(itemsElement.ValueKind)}.");
            }
            else
            {
                int index = 0;
                var seen = new HashSet<string>(StringComparer.Ordinal);
                foreach (var element in itemsElement.EnumerateArray())
                {
                    var spec = ParseItem(label, index, element, problems);
                    if (spec != null)
                    {
                        if (!seen.Add(spec.Id))
                            problems.Add($"{label}: {At(index, spec.Id)}: id is defined twice in this file.");
                        else
                            items.Add(spec);
                    }
                    index++;
                }
            }

            if (problems.Count > 0)
            {
                errors = problems;
                return false;
            }

            document = new ContentFileDocument(ContentFileSchema.Current, items);
            errors = Array.Empty<string>();
            return true;
        }
    }

    private static ContentItemSpec? ParseItem(
        string label, int index, JsonElement element, List<string> problems)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"{label}: item {index} must be a JSON object, not {Describe(element.ValueKind)}.");
            return null;
        }

        string? id = null;
        string? cloneOfName = null;
        int? cloneOfGuidHash = null;
        string? name = null;
        string? description = null;
        int? stack = null;
        ItemStatBonuses? statBonuses = null;
        bool hasCloneOf = false;
        bool ok = true;
        var fields = new HashSet<string>(StringComparer.Ordinal);

        foreach (var property in element.EnumerateObject())
        {
            if (!fields.Add(property.Name))
            {
                problems.Add(Field(label, index, id, property.Name, "is defined twice."));
                ok = false;
                continue;
            }
            switch (property.Name)
            {
                case "id":
                    if (property.Value.ValueKind != JsonValueKind.String)
                    {
                        problems.Add(Field(label, index, id, "id", $"must be a string, not {Describe(property.Value.ValueKind)}."));
                        ok = false;
                    }
                    else
                    {
                        id = property.Value.GetString();
                    }
                    break;

                case "cloneOf":
                    hasCloneOf = true;
                    if (property.Value.ValueKind == JsonValueKind.String)
                    {
                        cloneOfName = property.Value.GetString();
                        if (string.IsNullOrWhiteSpace(cloneOfName))
                        {
                            problems.Add(Field(label, index, id, "cloneOf", "is empty; give a spawnable name or a numeric GUID hash."));
                            ok = false;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number
                             && property.Value.TryGetInt32(out int hash))
                    {
                        // 0 is PrefabGUID.Empty, and to the builder that is indistinguishable
                        // from never having called CloneOf at all: PrefabRegistry.Register tests
                        // `_source.IsEmpty() && string.IsNullOrEmpty(_sourceName)` and throws.
                        // That throw is caught per item in ContentFileLoad.Run, so the file would
                        // be accepted whole and register every item except this one — the half-a-
                        // file state this parser's header forbids. It is a malformed file, not a
                        // registry disagreement, so it is refused here with the same both-forms
                        // message the empty-string arm above carries.
                        if (hash == 0)
                        {
                            problems.Add(Field(
                                label, index, id, "cloneOf",
                                "is 0, which is no prefab; give a spawnable name or a non-zero numeric GUID hash."));
                            ok = false;
                        }
                        else
                        {
                            cloneOfGuidHash = hash;
                        }
                    }
                    else if (property.Value.ValueKind == JsonValueKind.Number)
                    {
                        // A number the Int32 arm above could not take. Telling an admin who wrote
                        // 4037473093 that the field "must be a GUID hash (32-bit integer), not a
                        // number" answers a question they did not ask; say which number rule the
                        // one they wrote broke. See NumberProblem.
                        problems.Add(Field(
                            label, index, id, "cloneOf", NumberProblem(property.Value, guidHint: true)));
                        ok = false;
                    }
                    else
                    {
                        problems.Add(Field(
                            label, index, id, "cloneOf",
                            $"must be a spawnable name (string) or a GUID hash (32-bit integer), not {Describe(property.Value.ValueKind)}."));
                        ok = false;
                    }
                    break;

                case "name":
                    if (!TryText(label, index, id, property, "name", problems, out name)) ok = false;
                    break;

                case "description":
                    if (!TryText(label, index, id, property, "description", problems, out description)) ok = false;
                    break;

                case "stack":
                    if (property.Value.ValueKind != JsonValueKind.Number)
                    {
                        problems.Add(Field(label, index, id, "stack", $"must be an integer, not {Describe(property.Value.ValueKind)}."));
                        ok = false;
                    }
                    else if (!property.Value.TryGetInt32(out int amount))
                    {
                        problems.Add(Field(label, index, id, "stack", NumberProblem(property.Value, guidHint: false)));
                        ok = false;
                    }
                    else if (amount <= 0)
                    {
                        problems.Add(Field(label, index, id, "stack", $"must be positive; got {amount.ToString(CultureInfo.InvariantCulture)}."));
                        ok = false;
                    }
                    else
                    {
                        stack = amount;
                    }
                    break;

                case "statBonuses":
                    if (!TryStatBonuses(label, index, id, property.Value, problems, out statBonuses)) ok = false;
                    break;

                default:
                    problems.Add(
                        $"{label}: {At(index, id)}: unknown field '{property.Name}'; schema "
                        + $"{ContentFileSchema.Current} items know {Join(ContentFileSchema.ItemFields)}."
                        + Hint(property.Name));
                    ok = false;
                    break;
            }
        }

        if (id == null)
        {
            problems.Add($"{label}: item {index}: missing 'id'.");
            ok = false;
        }
        else if (!PrefabWire.TryValidateAscii(id, "id", out var idError, out bool idTooLong))
        {
            problems.Add($"{label}: {At(index, id)}: {idError}{ByteNote(id, idTooLong)}");
            ok = false;
        }

        if (!hasCloneOf)
        {
            problems.Add($"{label}: {At(index, id)}: missing 'cloneOf'; every item clones a prefab.");
            ok = false;
        }

        if (!ok) return null;
        return new ContentItemSpec(index, id!, cloneOfName, cloneOfGuidHash, name, description, stack, statBonuses);
    }

    private static bool TryStatBonuses(
        string label, int index, string? id, JsonElement value,
        List<string> problems, out ItemStatBonuses? bonuses)
    {
        bonuses = null;
        if (value.ValueKind != JsonValueKind.Object)
        {
            problems.Add(Field(label, index, id, "statBonuses", "must be an object."));
            return false;
        }
        float health = 0, physical = 0, spell = 0;
        bool ok = true;
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
        {
            string field = "statBonuses." + property.Name;
            if (!seen.Add(property.Name))
            {
                problems.Add(Field(label, index, id, field, "is defined twice."));
                ok = false;
                continue;
            }
            if (property.Name != "maxHealth" && property.Name != "physicalPower" && property.Name != "spellPower")
            {
                problems.Add(Field(label, index, id, field, "is unknown; expected maxHealth, physicalPower, spellPower."));
                ok = false;
                continue;
            }
            if (property.Value.ValueKind != JsonValueKind.Number
                || !property.Value.TryGetDouble(out double exactAmount)
                || double.IsNaN(exactAmount) || double.IsInfinity(exactAmount)
                || exactAmount < -10000 || exactAmount > 10000
                || !property.Value.TryGetSingle(out float amount)
                || float.IsNaN(amount) || float.IsInfinity(amount)
                || amount < -10000 || amount > 10000)
            {
                problems.Add(Field(label, index, id, field, "must be a finite number from -10000 to 10000."));
                ok = false;
                continue;
            }
            switch (property.Name)
            {
                case "maxHealth": health = amount; break;
                case "physicalPower": physical = amount; break;
                case "spellPower": spell = amount; break;
            }
        }
        if (!ok) return false;
        bonuses = new ItemStatBonuses(health, physical, spell);
        return true;
    }

    /// <summary>
    /// Optional display text. Same 1–63 ASCII bound the builder enforces (the packet field is
    /// 64 bytes including the terminator). A length refusal carries the measured byte count, so
    /// an admin who pasted a sentence knows how much to cut; a non-ASCII refusal carries no count
    /// and names the string, so an admin who pasted an em dash knows it is the character and not
    /// the length. See <see cref="ByteNote"/>.
    /// </summary>
    private static bool TryText(
        string label,
        int index,
        string? id,
        JsonProperty property,
        string field,
        List<string> problems,
        out string? text)
    {
        text = null;
        if (property.Value.ValueKind == JsonValueKind.Null) return true;
        if (property.Value.ValueKind != JsonValueKind.String)
        {
            problems.Add(Field(label, index, id, field, $"must be a string, not {Describe(property.Value.ValueKind)}."));
            return false;
        }

        var value = property.Value.GetString() ?? "";
        if (!PrefabWire.TryValidateAscii(value, field, out var error, out bool tooLong))
        {
            problems.Add($"{label}: {At(index, id)}: {error}{ByteNote(value, tooLong)}");
            return false;
        }

        text = value;
        return true;
    }

    /// <summary>
    /// The measured size, appended to a <b>length</b> refusal only — that is the one an admin
    /// answers by cutting characters. On a non-ASCII refusal the count is worse than useless:
    /// forty em dashes are forty characters and 120 UTF-8 bytes, and a note saying "120 bytes,
    /// limit 63" points at a length that is not the problem and would still be refused at twenty
    /// characters. <paramref name="tooLong"/> is <c>PrefabWire.TryValidateAscii</c>'s own
    /// discriminator, so the two refusals can never drift apart here. UTF-8 rather than ASCII so
    /// an over-length string with any non-ASCII in it reports the bytes it really is instead of
    /// one byte per replaced char.
    /// </summary>
    private static string ByteNote(string value, bool tooLong) =>
        tooLong
            ? $" (this one is {Encoding.UTF8.GetByteCount(value).ToString(CultureInfo.InvariantCulture)} bytes, limit {ContentFileLimits.MaxTextBytes})."
            : "";

    /// <summary>
    /// R68 finding 4. A JSON number that <c>TryGetInt32</c> refuses is refused for one of two
    /// reasons, and "not a number" is neither of them: the value is outside the Int32 range, or it
    /// is not a whole number. Both messages carry the value the admin wrote and the rule it broke.
    ///
    /// <para>The out-of-range case has one overwhelmingly common cause on <c>cloneOf</c>: a
    /// <c>PrefabGUID</c> hash is a <b>signed</b> 32-bit value, several community GUID tables
    /// publish the unsigned spelling, and an admin who thinks a hash "looks negative by mistake"
    /// adds 2^32 to it themselves. So when the value fits in a <c>uint</c> the refusal computes
    /// the signed equivalent and prints it, which turns the message into the edit
    /// (<paramref name="guidHint"/>). <c>stack</c> gets the range without the hash lecture.</para>
    ///
    /// <para>What this field takes is decided on the literal as written, not on its value: JSON's
    /// <c>1.0</c> and <c>1e3</c> are integers in arithmetic but neither is a spelling this field
    /// takes, and <c>TryGetInt32</c> refuses both. R69 finding 11: deciding on the literal is
    /// right, but "which is not a whole number" is then <b>false of the number the admin wrote</b>
    /// — <c>-257494203.0</c> is what <c>json.dump</c> and a spreadsheet export produce from the
    /// correct hash, and it is whole. So a whole-valued value written with a decimal point or an
    /// exponent is refused for its spelling and handed the integer literal to write instead
    /// (from <c>TryGetDecimal</c>, which is exact for every spelling that reaches this in
    /// practice); only a genuinely fractional value is refused as not whole. Past
    /// <c>decimal</c>'s range the value is still judged whole or not, but no spelling is offered:
    /// a <c>double</c>'s exact decimal expansion is not the number that was written.</para>
    /// </summary>
    private static string NumberProblem(JsonElement value, bool guidHint)
    {
        var raw = value.GetRawText();
        var shown = Shorten(raw);

        if (!IsWholeNumberLiteral(raw))
        {
            var spelling = NumberSpelling(raw);
            if (spelling is not null && IsWholeValued(value, out string? integerForm))
            {
                var write = integerForm is null
                    ? "write it as an integer literal"
                    : $"write it as {Shorten(integerForm)}";
                return guidHint
                    ? $"is {shown}, which is written {spelling}; this field takes a spawnable name or "
                      + $"an integer literal, so {write}."
                    : $"is {shown}, which is written {spelling}; this field takes an integer literal, so {write}.";
            }

            return guidHint
                ? $"is {shown}, which is a fractional value; give a spawnable name or a whole 32-bit GUID hash."
                : $"is {shown}, which is a fractional value; this field takes a whole number.";
        }

        var range = $"outside the 32-bit range {int.MinValue.ToString(CultureInfo.InvariantCulture)} "
                    + $"to {int.MaxValue.ToString(CultureInfo.InvariantCulture)}";
        if (!guidHint) return $"is {shown}, which is {range}.";

        if (ulong.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out ulong unsigned)
            && unsigned <= uint.MaxValue)
        {
            var signed = unchecked((int)(uint)unsigned).ToString(CultureInfo.InvariantCulture);
            return $"is {shown}, which is {range}; PrefabGUID hashes are signed 32-bit values, "
                   + $"so {shown} is written {signed}.";
        }

        return $"is {shown}, which is {range}; PrefabGUID hashes are signed 32-bit values.";
    }

    /// <summary>
    /// Which of the two spellings this field does not take was used, for the refusal sentence.
    /// <c>null</c> when neither is present, which for a literal <see cref="IsWholeNumberLiteral"/>
    /// rejected means something else is wrong with it and the general refusal is the honest one.
    /// </summary>
    private static string? NumberSpelling(string raw)
    {
        bool point = raw.IndexOf('.') >= 0;
        bool exponent = raw.IndexOf('e') >= 0 || raw.IndexOf('E') >= 0;
        if (point && exponent) return "with a decimal point and an exponent";
        if (point) return "with a decimal point";
        if (exponent) return "with an exponent";
        return null;
    }

    /// <summary>
    /// Is the <b>value</b> a whole one, whatever its spelling — and if it is, what is the integer
    /// literal for it. <c>decimal</c> is exact for every spelling a content file realistically
    /// carries, so its answer is the one that becomes the suggested edit. Outside its range a
    /// <c>double</c> still says whole or not, but its exact decimal expansion is not what the
    /// admin wrote (<c>1e30</c> expands to 1000000000000000019884624838656), so no edit is offered
    /// and <paramref name="integerForm"/> stays <c>null</c>.
    /// </summary>
    private static bool IsWholeValued(JsonElement value, out string? integerForm)
    {
        integerForm = null;

        if (value.TryGetDecimal(out decimal exact))
        {
            if (decimal.Truncate(exact) != exact) return false;
            integerForm = exact.ToString("0", CultureInfo.InvariantCulture);
            return true;
        }

        return value.TryGetDouble(out double approximate)
               && !double.IsNaN(approximate)
               && !double.IsInfinity(approximate)
               && Math.Floor(approximate) == approximate;
    }

    /// <summary>An optionally-signed run of digits and nothing else — no point, no exponent.</summary>
    private static bool IsWholeNumberLiteral(string raw)
    {
        int i = raw.Length > 0 && (raw[0] == '-' || raw[0] == '+') ? 1 : 0;
        if (i >= raw.Length) return false;
        for (; i < raw.Length; i++)
        {
            if (raw[i] < '0' || raw[i] > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// JSON puts no bound on the digits in a number, and the refusal quotes what was written, so a
    /// pasted blob cannot become a thousand-character log line. Nothing decides anything from the
    /// shortened form: it is display only.
    /// </summary>
    private static string Shorten(string raw) =>
        raw.Length <= 40 ? raw : raw.Substring(0, 40) + "...";

    private static string Field(string label, int index, string? id, string field, string what) =>
        $"{label}: {At(index, id)}: field '{field}' {what}";

    private static string At(int index, string? id) =>
        id == null ? $"item {index.ToString(CultureInfo.InvariantCulture)}"
                   : $"item {index.ToString(CultureInfo.InvariantCulture)} ('{id}')";

    private static string Join(IReadOnlyList<string> names) => string.Join(", ", names);

    /// <summary>
    /// A field the schema does not have but an author plausibly expected. Phase 1 has no item
    /// icon because the item builder has none (only <c>DefineBuff</c> takes <c>Icon</c>), and
    /// buffs are not in phase 1 at all; saying so is cheaper than a support round trip.
    /// </summary>
    private static string Hint(string name) => name switch
    {
        "icon" => " Items have no icon: the clone keeps its source prefab's sprite (only DefineBuff takes an icon).",
        "maxAmount" => " Stack size is 'stack'.",
        "clone" or "cloneof" or "source" => " The field is 'cloneOf'.",
        "buffs" or "buff" => " Phase 1 defines items only.",
        _ => "",
    };

    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "an object",
        JsonValueKind.Array => "an array",
        JsonValueKind.String => "a string",
        JsonValueKind.Number => "a number",
        JsonValueKind.True or JsonValueKind.False => "a boolean",
        JsonValueKind.Null => "null",
        _ => "nothing",
    };
}

/// <summary>The bounds the file format shares with the definition packet, named once.</summary>
internal static class ContentFileLimits
{
    /// <summary>1–63 ASCII bytes, the same bound <c>DefineItem</c> / <c>Named</c> / <c>Described</c> enforce.</summary>
    public const int MaxTextBytes = PrefabWire.AsciiMaxChars;
}
