using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.LayoutGen;

/// <summary>
/// What a run of the resolver adds up to, and what that means for the exit code and the summary
/// line. Pure: resolutions in, counts and strings out — no file system, no console, no dump.
///
/// <para><b>Why this is its own type (R53 finding 1).</b> The tally used to live as three local
/// ints inside <c>Main</c>, and one of them — the count of declarations the dump could not
/// resolve — was incremented and then never used for anything that mattered. Only a
/// <em>disagreement</em> returned 2, so a declaration naming a type or a field the dump no longer
/// contains printed one <c>?</c> line among forty <c>ok</c> lines and the tool exited 0 with "the
/// dump agrees with every value". That is not an edge case: a game update that renames
/// <c>ProjectM.Health</c> or drops <c>Health.IsDead</c> produces exactly this, and it produces it
/// on the one command an operator runs before trusting a raw write. The decision now lives in a
/// type the test project compiles, so the exit code and the wording are asserted rather than
/// read.</para>
///
/// <para><b>Three outcomes, not two.</b> <see cref="Agreements"/> is a value the dump confirmed;
/// <see cref="Disagreements"/> is a value the dump contradicts; <see cref="Unresolved"/> is a
/// declaration whose dump <em>was</em> loaded and which that dump cannot answer at all. The first
/// two were always distinguished; the third is the game-update shape and now fails the same way a
/// disagreement does. <see cref="Skipped"/> is the separate, benign case — a declaration for a
/// side whose dump this run was not given — and never fails anything, because that is what lets
/// the game-free half of <c>--check</c> run on a machine with no dump at all.</para>
/// </summary>
public sealed class LayoutCheckTally
{
    private LayoutCheckTally(
        int dumpsLoaded,
        int agreements,
        int disagreements,
        int skipped,
        List<LayoutEmitter.Resolution> unresolved)
    {
        DumpsLoaded = dumpsLoaded;
        Agreements = agreements;
        Disagreements = disagreements;
        Skipped = skipped;
        Unresolved = unresolved;
    }

    /// <summary>How many dumps this run loaded. Zero means the game-free half only.</summary>
    public int DumpsLoaded { get; }

    /// <summary>Declarations the dump resolved to exactly the declared value.</summary>
    public int Agreements { get; }

    /// <summary>Declarations the dump resolved to a different value.</summary>
    public int Disagreements { get; }

    /// <summary>Declarations whose side had no dump on this run. Not a drift signal.</summary>
    public int Skipped { get; }

    /// <summary>
    /// Declarations whose dump was loaded and could not answer them: a vanished type, a renamed
    /// field, an ambiguous name, a pattern that matches nothing.
    /// </summary>
    public IReadOnlyList<LayoutEmitter.Resolution> Unresolved { get; }

    /// <summary>Declarations the dump was actually asked about.</summary>
    public int Checked => Agreements + Disagreements + Unresolved.Count;

    /// <summary>
    /// True when the dump half found something wrong — a contradicted value <b>or</b> a value it
    /// could not look at. Both are exit 2; neither writes a file.
    /// </summary>
    public bool DumpFailed => Disagreements > 0 || Unresolved.Count > 0;

    /// <summary>
    /// The <c>resolved:</c> stamp the generated file may carry. Only a run that loaded a dump,
    /// resolved something, and left nothing unresolved <em>or</em> skipped may claim it.
    /// </summary>
    public bool FullyResolved => DumpsLoaded > 0 && Agreements > 0 && Unresolved.Count == 0
        && Skipped == 0 && Disagreements == 0;

    /// <summary>Tally <paramref name="resolutions"/>, treating any side in <paramref name="sidesWithDumps"/> as asked.</summary>
    public static LayoutCheckTally Of(
        IReadOnlyList<LayoutEmitter.Resolution> resolutions,
        IReadOnlyCollection<string> sidesWithDumps)
    {
        if (resolutions is null) throw new ArgumentNullException(nameof(resolutions));
        if (sidesWithDumps is null) throw new ArgumentNullException(nameof(sidesWithDumps));

        var asked = new HashSet<string>(sidesWithDumps, StringComparer.Ordinal);
        int agreements = 0, disagreements = 0, skipped = 0;
        var unresolved = new List<LayoutEmitter.Resolution>();
        foreach (var r in resolutions)
        {
            if (!asked.Contains(r.Declaration.Side)) { skipped++; continue; }
            if (!r.Resolved) unresolved.Add(r);
            else if (r.Agrees) agreements++;
            else disagreements++;
        }

        return new LayoutCheckTally(asked.Count, agreements, disagreements, skipped, unresolved);
    }

    /// <summary>
    /// The clause <c>--check</c> prints about the dump half, built from the counts rather than
    /// from an assumption about how the code got here.
    ///
    /// <para><b>False-proof by construction (R53 finding 1).</b> The old line was a two-way
    /// ternary on "was a dump given", so the words "the dump agrees with every value" were
    /// printed by a branch that had not looked at whether every value <em>was</em> checked. The
    /// only sentence that can claim agreement now is the one reached with nothing unresolved,
    /// nothing skipped and nothing disagreeing, and it says the number it agreed about.</para>
    /// </summary>
    public string Verdict()
    {
        if (DumpsLoaded == 0) return "no dump given, values unconfirmed";
        if (Disagreements > 0) return N(Disagreements) + " disagree with the dump";
        if (Unresolved.Count > 0)
        {
            return N(Agreements) + " agree, " + N(Unresolved.Count)
                + " could not be resolved in the dump";
        }
        if (Skipped > 0)
        {
            return "the dump agrees with all " + N(Agreements) + " it was asked about, "
                + N(Skipped) + " unchecked (no dump for their side)";
        }
        if (Agreements == 0) return "no value was checked against a dump";
        return "the dump agrees with all " + N(Agreements);
    }

    private static string N(int count) =>
        count.ToString(CultureInfo.InvariantCulture) + " value" + (count == 1 ? "" : "s");

    /// <summary>
    /// Where a human goes to fix <paramref name="declaration"/>: the declarations file and the
    /// line in it. A report that names only the constant leaves the reader grepping.
    /// </summary>
    public static string Where(string declarationsPath, LayoutDeclaration declaration)
    {
        var file = string.IsNullOrEmpty(declarationsPath) ? "layout.txt" : declarationsPath;
        return declaration.LineNumber > 0
            ? file + ":" + declaration.LineNumber.ToString(CultureInfo.InvariantCulture)
            : file;
    }
}

/// <summary>
/// Is the metadata this check resolves against a dump <em>of the game on this machine</em>
/// (R54 finding 1, job FLAY3)?
///
/// <para><b>The hole this closes.</b> Every number in <c>layout.txt</c> is a raw memory write,
/// and the whole gate rests on one artifact: the type listing in <c>&lt;metadata&gt;\&lt;side&gt;</c>
/// (<c>PROGENITOR_METADATA_DIR</c>, default <c>&lt;workspace&gt;\_tools\interop-dump</c>).
/// That file is exported by hand, lives outside the repository by decision, and is
/// regenerated by nothing — not by a game update, not by <c>deploy.ps1</c>, not by the build. The
/// only policy the gate had for it was <c>Test-Path</c>. So the first time Stunlock ships a patch,
/// every declaration resolves happily against a dump of the <em>previous</em> build, the tool
/// prints "the dump agrees with all 43 values" and exits 0, and 43 offsets ship having been
/// checked against nothing. The failure is silent and it arrives on exactly the day the gate
/// exists for.</para>
///
/// <para><b>The rule, fail-closed.</b> A dump may not be older than the binary it claims to
/// describe. The dump carries no build stamp of its own — the export writes an image list and
/// nothing else, and the files beside it carry no version either —
/// so the identity of a dump is the identity of the <c>GameAssembly.dll</c> it was taken from:
/// last-write time (to the second, UTC) and byte length. <c>--check</c> is given that binary per
/// side with <c>--game-assembly</c> and refuses, with exit 2, when the dump is the older of the
/// two, when the named binary is not there at all, or when the binary differs from the identity
/// the generated header recorded. A side whose binary was <em>not named</em> is reported as
/// unchecked rather than passing quietly, because a gate that can be disabled by dropping an
/// argument is a gate nobody notices losing.</para>
///
/// <para><b>And a header that records nothing is that same gate, dropped (job FLAY4, R55
/// finding 4).</b> For one commit the identity rule could not fire at all: the checked-in header
/// had no <c>game:</c> line, <c>--check</c> never writes a file so it could never add one, the
/// staleness diff deliberately ignores the line so it never noticed the absence, and the
/// documented generate recipe omitted <c>--game-assembly</c> — so <c>recorded</c> was empty,
/// <c>was.Known</c> was false for every side, and the branch below was dead while the run printed
/// agreement over 43 values. The fix is symmetric with the one above: under <c>--check</c>, a side
/// whose binary <em>was</em> named and whose identity the header does not record is a failure,
/// with a remedy that says to regenerate with <c>--game-assembly</c>. Only a rule that fails when
/// it cannot run is a rule.</para>
///
/// <para><b>And a generate records every side, not only the sides it was handed (job FLAY5,
/// R56 finding 5).</b> The rule above made a half-recorded header a deploy-stopping refusal on
/// every machine, and a half-recorded header was trivial to produce: a generate on a machine with
/// no test-server tree names <c>client=</c> only, and the emitted <c>game:</c> line used to be
/// built from that run's measurements alone, so it silently dropped <c>server=</c>. The identity a
/// run did not measure is carried forward from the checked-in header exactly as <c>build:</c> is —
/// see <see cref="MergeInto"/> — and the emitted value is read back through
/// <see cref="ParseHeaderValue"/> so that a run which would drop a recorded side refuses instead of
/// writing the file. Carrying forward is the fix; the read-back is what keeps it from quietly
/// stopping being the fix.</para>
///
/// <para>Pure except for two <c>FileInfo</c> stats per side, which is what makes it testable over
/// fabricated files with fabricated timestamps.</para>
/// </summary>
public sealed class DumpFreshness
{
    /// <summary>
    /// What identifies one side's game binary. Not a hash: the point is to notice a game update
    /// cheaply, and a 100 MB hash on every deploy would be paid every time to answer a question
    /// two stats answer.
    /// </summary>
    public readonly struct Identity : IEquatable<Identity>
    {
        public Identity(DateTime writtenUtc, long length)
        {
            WrittenUtc = Truncate(writtenUtc);
            Length = length;
            Known = true;
        }

        /// <summary>Last write time, UTC, truncated to the second (a header cannot carry ticks).</summary>
        public DateTime WrittenUtc { get; }

        public long Length { get; }

        /// <summary>False for <c>default</c>: nothing was measured or parsed.</summary>
        public bool Known { get; }

        public string Format() => Known
            ? WrittenUtc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture)
                + "/" + Length.ToString(CultureInfo.InvariantCulture)
            : "unrecorded";

        public static bool TryParse(string text, out Identity identity)
        {
            identity = default;
            if (string.IsNullOrEmpty(text)) return false;
            int slash = text.LastIndexOf('/');
            if (slash <= 0 || slash == text.Length - 1) return false;
            if (!DateTime.TryParseExact(
                    text.Substring(0, slash), "yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var when))
                return false;
            if (!long.TryParse(
                    text.Substring(slash + 1), NumberStyles.Integer, CultureInfo.InvariantCulture,
                    out long length))
                return false;
            identity = new Identity(when, length);
            return true;
        }

        public bool Equals(Identity other) =>
            Known == other.Known && WrittenUtc == other.WrittenUtc && Length == other.Length;

        public override bool Equals(object? obj) => obj is Identity other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(Known, WrittenUtc, Length);

        private static DateTime Truncate(DateTime value)
        {
            var utc = value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime();
            return new DateTime(utc.Ticks - (utc.Ticks % TimeSpan.TicksPerSecond), DateTimeKind.Utc);
        }
    }

    private DumpFreshness(
        List<string> failures,
        List<string> notes,
        SortedDictionary<string, Identity> measured,
        List<string> remedies)
    {
        Failures = failures;
        Notes = notes;
        Measured = measured;
        Remedies = remedies;
    }

    /// <summary>Plain sentences, one per side that failed. Non-empty means exit 2.</summary>
    public IReadOnlyList<string> Failures { get; }

    /// <summary>Sentences worth printing that are not a verdict — chiefly "nobody named a binary".</summary>
    public IReadOnlyList<string> Notes { get; }

    /// <summary>The identity measured for each side that named a binary this run.</summary>
    public IReadOnlyDictionary<string, Identity> Measured { get; }

    /// <summary>
    /// What to do about <see cref="Failures"/>, in the order the failures were found and without
    /// repeats. Two different failures want two different procedures — a dump older than the
    /// binary wants a re-dump (<see cref="HowToRefresh"/>), a header that records no identity
    /// wants a regenerate with <c>--game-assembly</c> (<see cref="HowToRecord"/>) — and printing
    /// the re-dump procedure under a missing-identity refusal sends the reader to re-export
    /// metadata that is perfectly current (job FLAY4, R55 finding 4).
    /// </summary>
    public IReadOnlyList<string> Remedies { get; }

    public bool Failed => Failures.Count > 0;

    /// <summary>The sides that failed, for a caller that wants to name them in one sentence.</summary>
    public IReadOnlyList<string> FailedSides { get; private set; } = Array.Empty<string>();

    /// <summary>Every side judged this run — i.e. every side a dump was loaded for.</summary>
    public IReadOnlyList<string> Sides { get; private set; } = Array.Empty<string>();

    /// <summary>
    /// Every side <c>--game-assembly</c> named a path for, whether or not that side ended up
    /// measured. The difference between "you gave me no binary" and "you gave me a binary I could
    /// not use" is the whole content of the <c>--check</c> line's reason clause (job FLAY7).
    /// </summary>
    public IReadOnlyList<string> NamedBinaries { get; private set; } = Array.Empty<string>();

    /// <summary>The <c>game:</c> header value for the identities measured this run, or "".</summary>
    /// <remarks>
    /// What this run saw, which is what the console line reports. It is <em>not</em> what gets
    /// written to the header: a generate that named one side would then erase the other's record.
    /// <see cref="MergeInto"/> is the one that decides the header (job FLAY5, R56 finding 5).
    /// </remarks>
    public string HeaderValue() => Format(Measured);

    /// <summary>
    /// The outcome of merging this run's measurements into the identities the checked-in header
    /// already records: the value to emit, plus the two things a caller has to be told about it.
    /// </summary>
    public sealed class MergedIdentities
    {
        internal MergedIdentities(string headerValue, IReadOnlyList<string> dropped,
            IReadOnlyList<string> unrecorded)
        {
            HeaderValue = headerValue;
            Dropped = dropped;
            Unrecorded = unrecorded;
        }

        /// <summary>The <c>game:</c> value to write. "" means "record nothing", as before.</summary>
        public string HeaderValue { get; }

        /// <summary>
        /// Sides the checked-in header records that <see cref="HeaderValue"/> does not carry —
        /// read back out of the emitted text rather than assumed from the inputs, so a formatting
        /// bug counts as a drop too. Non-empty is a refusal: writing the file would disarm the
        /// identity rule for those sides on every machine that has their binary.
        /// </summary>
        public IReadOnlyList<string> Dropped { get; }

        /// <summary>
        /// Sides this run judged for which neither a measurement nor a record exists, so the
        /// emitted header will not cover them. Not a failure — a first generate on a machine with
        /// one binary has to be possible — but never silent.
        /// </summary>
        public IReadOnlyList<string> Unrecorded { get; }
    }

    /// <summary>
    /// Decide the <c>game:</c> value a generate should write, given the value the checked-in file
    /// carries. Sides measured this run are re-recorded; every other side keeps what the header
    /// already said, carried forward the way <c>build:</c> is and for the same reason — this
    /// machine may not have that side's binary, and "I could not measure it" is not "it does not
    /// exist" (job FLAY5, R56 finding 5).
    /// </summary>
    /// <param name="recordedHeaderValue">
    /// The header's current <c>game:</c> value, verbatim. When nothing was measured it is returned
    /// verbatim too, so a generate that names no binary at all reproduces the file byte for byte.
    /// </param>
    public MergedIdentities MergeInto(string recordedHeaderValue)
    {
        var recorded = ParseHeaderValue(recordedHeaderValue ?? "");

        string value;
        if (Measured.Count == 0)
        {
            value = recordedHeaderValue ?? "";
        }
        else
        {
            var merged = new SortedDictionary<string, Identity>(StringComparer.Ordinal);
            foreach (var pair in recorded) if (pair.Value.Known) merged[pair.Key] = pair.Value;
            foreach (var pair in Measured) merged[pair.Key] = pair.Value;
            value = Format(merged);
        }

        // Read the answer back out of the text that will actually be written. Comparing the
        // dictionaries would only prove the loop above is the loop above; parsing the emitted
        // value proves the file will carry what this says it carries.
        var carried = ParseHeaderValue(value);
        var dropped = new List<string>();
        foreach (var pair in recorded)
        {
            if (!pair.Value.Known) continue;
            if (!carried.TryGetValue(pair.Key, out var still) || !still.Known) dropped.Add(pair.Key);
        }

        var unrecorded = new List<string>();
        foreach (var side in Sides)
            if (!carried.TryGetValue(side, out var id) || !id.Known) unrecorded.Add(side);

        return new MergedIdentities(value, dropped, unrecorded);
    }

    /// <summary>
    /// The <c>measured:</c> header value for this run: the sides it actually stat'd a
    /// <c>GameAssembly.dll</c> for, space separated and ordinally sorted, or "" when it measured
    /// nothing.
    /// </summary>
    /// <remarks>
    /// The provenance of the <c>game:</c> line, and the half FLAY5 left unsaid (R57 finding 6).
    /// Since a side's identity is only rewritten by a run that measures it, every entry in
    /// <c>game:</c> was measured by <em>some</em> run — so what the file has to record is which
    /// sides the run that last wrote that line measured. Every other side in <c>game:</c> was
    /// carried forward, possibly from a run several game updates ago. A run that measures nothing
    /// writes no new identity and so carries this line forward verbatim too (job FLAY6).
    /// </remarks>
    public string MeasuredSidesValue() => string.Join(" ", Sorted(Measured.Keys));

    /// <summary>
    /// A <c>game:</c> value with each side marked <c>(measured)</c> when this run stat'd that
    /// side's binary and <c>(carried)</c> when the value came out of the checked-in header.
    /// This is what the console prints on a <em>generate</em>, over the value that will actually
    /// be written — before job FLAY6 it printed <see cref="HeaderValue"/>, which on a one-sided
    /// generate is a different string from the one the file gets (R57 finding 6). A check prints
    /// <see cref="DescribeChecked"/> instead: "measured" is the right word for a value being
    /// recorded and the wrong one for a value being checked (job FLAY7, R58 finding 10).
    /// </summary>
    public string Describe(string headerValue)
    {
        var identities = ParseHeaderValue(headerValue ?? "");
        if (identities.Count == 0) return "(nothing recorded)";
        var parts = new List<string>(identities.Count);
        foreach (var pair in identities)
            parts.Add(pair.Key + "=" + pair.Value.Format()
                      + (Measured.ContainsKey(pair.Key) ? " (measured)" : " (carried)"));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// One sentence naming which sides of <paramref name="headerValue"/> this run measured and
    /// which it took on trust — the generate half. Both modes get a provenance line, because "the
    /// identities being compared" and "the identities being written" are equally worth one and
    /// neither used to have one (R57 finding 6); the check half is
    /// <see cref="CheckProvenanceSummary"/>, which says compared rather than measured so its
    /// counts match the per-side marks (job FLAY7, R58 finding 10).
    /// </summary>
    public string ProvenanceSummary(string headerValue)
    {
        var identities = ParseHeaderValue(headerValue ?? "");
        var measured = new List<string>();
        var carried = new List<string>();
        foreach (var pair in identities)
            (Measured.ContainsKey(pair.Key) ? measured : carried).Add(pair.Key);
        return "measured this run: " + Join(measured)
               + "; carried from the header: " + Join(carried);
    }

    /// <summary>
    /// The annotation a side gets on the <c>--check</c> line: <c>(compared)</c> when this run was
    /// handed that side's <c>GameAssembly.dll</c> and stat'd it, and the reason it was not
    /// otherwise. Public so the suite asserts the words rather than a paraphrase of them.
    /// </summary>
    public const string ComparedMark = "(compared)";

    /// <summary>
    /// The other half of <see cref="ComparedMark"/> for the ordinary case: the side's binary was
    /// never named, so nothing on this machine was there to check it against.
    /// </summary>
    public const string NotComparedMark = "(recorded, not compared: no --game-assembly for this side)";

    /// <summary>
    /// The other other half: a binary <em>was</em> named for this side and the comparison still
    /// did not happen — no dump was loaded for it, or the file named is not there (which is a
    /// refusal of its own, printed in full elsewhere). Distinct from
    /// <see cref="NotComparedMark"/> because telling an operator who passed
    /// <c>--game-assembly</c> that they passed none sends them to add an argument they already
    /// gave.
    /// </summary>
    public const string NotCheckedMark = "(recorded, not compared: this side's binary was not checked)";

    /// <summary>
    /// The verb the <c>--check</c> console line opens with. "compared" is only earned by a run
    /// that measured at least one side; a run given no <c>--game-assembly</c> at all stat'd no
    /// game binary and says so (job FLAY7, R58 finding 10).
    /// </summary>
    /// <remarks>
    /// FLAY6 printed <c>game identity compared:</c> unconditionally, and the repository's own
    /// recipe — and every test — runs <c>--check</c> with no <c>--game-assembly</c>. So the line
    /// an operator reads most often announced two identities as compared, followed immediately by
    /// the honest line saying nothing was measured. One of the two had to go, and it was not the
    /// honest one.
    /// </remarks>
    public string CheckVerb()
    {
        if (Measured.Count > 0) return "game identity compared";
        return NamedBinaries.Count == 0
            ? "game identity recorded (not compared: no --game-assembly given)"
            : "game identity recorded (not compared: no side's binary was checked)";
    }

    /// <summary>The mark <see cref="DescribeChecked"/> puts on one side.</summary>
    public string MarkFor(string side)
    {
        if (Measured.ContainsKey(side)) return ComparedMark;
        foreach (var named in NamedBinaries)
            if (string.Equals(named, side, StringComparison.Ordinal)) return NotCheckedMark;
        return NotComparedMark;
    }

    /// <summary>
    /// The <c>--check</c> counterpart of <see cref="Describe"/>: each side marked
    /// <see cref="ComparedMark"/>, <see cref="NotComparedMark"/> or <see cref="NotCheckedMark"/>.
    /// When this run neither measured a side nor was handed a binary for one, the annotation is
    /// dropped entirely — every side would carry the same negative mark, and
    /// <see cref="CheckVerb"/> has already said it once for the whole line.
    /// </summary>
    public string DescribeChecked(string headerValue)
    {
        var identities = ParseHeaderValue(headerValue ?? "");
        if (identities.Count == 0) return "(nothing recorded)";
        if (Measured.Count == 0 && NamedBinaries.Count == 0) return Format(identities);
        var parts = new List<string>(identities.Count);
        foreach (var pair in identities)
            parts.Add(pair.Key + "=" + pair.Value.Format() + " " + MarkFor(pair.Key));
        return string.Join(" ", parts);
    }

    /// <summary>
    /// The <c>--check</c> counterpart of <see cref="ProvenanceSummary"/>, in the same words the
    /// per-side marks use, so the two lines cannot disagree: the sides named here as compared are
    /// exactly the sides <see cref="DescribeChecked"/> marks <see cref="ComparedMark"/>.
    /// </summary>
    public string CheckProvenanceSummary(string headerValue)
    {
        var identities = ParseHeaderValue(headerValue ?? "");
        var compared = new List<string>();
        var recorded = new List<string>();
        foreach (var pair in identities)
            (Measured.ContainsKey(pair.Key) ? compared : recorded).Add(pair.Key);
        return "compared this run: " + Join(compared)
               + "; recorded but not compared: " + Join(recorded);
    }

    private static List<string> NonEmptyKeys(IReadOnlyDictionary<string, string> paths)
    {
        var keys = new List<string>();
        foreach (var pair in paths)
            if (!string.IsNullOrEmpty(pair.Value)) keys.Add(pair.Key);
        return keys;
    }

    private static string Join(List<string> sides) =>
        sides.Count == 0 ? "none" : string.Join(", ", sides);

    /// <summary>
    /// What to do about a side <see cref="MergedIdentities.Dropped"/> or
    /// <see cref="MergedIdentities.Unrecorded"/> names: hand this run that side's binary too.
    /// </summary>
    public static string HowToName(string side) =>
        "add --game-assembly " + side + "=<path to the " + side + " GameAssembly.dll> to this run.";

    private static string Format(IReadOnlyDictionary<string, Identity> identities)
    {
        if (identities.Count == 0) return "";
        var parts = new List<string>(identities.Count);
        foreach (var pair in identities) parts.Add(pair.Key + "=" + pair.Value.Format());
        return string.Join(" ", parts);
    }

    /// <summary>Parse a <c>game:</c> header value back into per-side identities.</summary>
    public static SortedDictionary<string, Identity> ParseHeaderValue(string headerValue)
    {
        var parsed = new SortedDictionary<string, Identity>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(headerValue)) return parsed;
        foreach (var part in headerValue.Split(new[] { ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            int eq = part.IndexOf('=');
            if (eq <= 0) continue;
            if (Identity.TryParse(part.Substring(eq + 1), out var id))
                parsed[part.Substring(0, eq)] = id;
        }
        return parsed;
    }

    /// <summary>
    /// Judge every side that has a dump.
    /// </summary>
    /// <param name="dumpPaths">side -&gt; metadata file actually loaded this run.</param>
    /// <param name="binaryPaths">side -&gt; GameAssembly.dll named by <c>--game-assembly</c>.</param>
    /// <param name="recorded">side -&gt; identity the checked-in generated header records.</param>
    /// <param name="recordedIsBinding">
    /// Whether a recorded identity that differs from the measured one is a failure. True under
    /// <c>--check</c>; false when generating, because a regenerate after a re-dump is precisely
    /// how a new identity gets recorded.
    /// </param>
    /// <param name="stats">
    /// How a path becomes (exists, last write UTC, length). The real one is
    /// <see cref="FileStat"/>; the suite hands in fabricated files.
    /// </param>
    public static DumpFreshness Of(
        IReadOnlyDictionary<string, string> dumpPaths,
        IReadOnlyDictionary<string, string> binaryPaths,
        IReadOnlyDictionary<string, Identity> recorded,
        bool recordedIsBinding,
        Func<string, (bool Exists, DateTime WrittenUtc, long Length)>? stats = null)
    {
        if (dumpPaths is null) throw new ArgumentNullException(nameof(dumpPaths));
        if (binaryPaths is null) throw new ArgumentNullException(nameof(binaryPaths));
        stats ??= FileStat;

        var failures = new List<string>();
        var failedSides = new List<string>();
        var notes = new List<string>();
        var remedies = new List<string>();
        var measured = new SortedDictionary<string, Identity>(StringComparer.Ordinal);

        void Refuse(string side, string sentence, string remedy)
        {
            failures.Add(sentence);
            failedSides.Add(side);
            if (!remedies.Contains(remedy)) remedies.Add(remedy);
        }

        foreach (var side in Sorted(dumpPaths.Keys))
        {
            var dumpPath = dumpPaths[side];
            if (!binaryPaths.TryGetValue(side, out var binaryPath) || string.IsNullOrEmpty(binaryPath))
            {
                notes.Add("the " + side + " dump was not checked for freshness: no --game-assembly"
                    + " path was given for the " + side + " side, so nothing compared "
                    + dumpPath + " against the game it claims to describe.");
                continue;
            }

            var binary = stats(binaryPath);
            if (!binary.Exists)
            {
                Refuse(side, "the " + side + " GameAssembly.dll is not at " + binaryPath
                    + ", so the " + side + " dump could not be checked against the game it claims"
                    + " to describe.", HowToRefresh(side));
                continue;
            }

            var dump = stats(dumpPath);
            if (!dump.Exists)
            {
                Refuse(side, "the " + side + " dump is not at " + dumpPath + " any more.",
                    HowToRefresh(side));
                continue;
            }

            var binaryIdentity = new Identity(binary.WrittenUtc, binary.Length);
            measured[side] = binaryIdentity;
            var dumpWritten = new Identity(dump.WrittenUtc, dump.Length).WrittenUtc;

            if (dumpWritten < binaryIdentity.WrittenUtc)
            {
                Refuse(side, "the " + side + " metadata is older than the game binary it claims"
                    + " to describe: " + dumpPath + " was written "
                    + Stamp(dumpWritten) + " and " + binaryPath + " was written "
                    + Stamp(binaryIdentity.WrittenUtc)
                    + ". The game was updated after the dump was made, so every declared value"
                    + " would be checked against the previous build.", HowToRefresh(side));
                continue;
            }

            if (!recordedIsBinding) continue;

            Identity was = default;
            bool hasRecord = recorded != null && recorded.TryGetValue(side, out was) && was.Known;

            // The rule the check is FOR, and the reason it was dead for a commit (R55 finding 4,
            // job FLAY4). A side whose binary this run was handed but whose identity the header
            // does not record cannot be compared against anything, and "no record" is exactly
            // what a header written without --game-assembly produces — indistinguishable, from
            // inside, from a header that predates the line. Falling through there is how the
            // identity rule silently de-arms: the mtime rule keeps passing, the check keeps
            // printing agreement, and nothing ever says the strong half is switched off. So a
            // missing record is itself a refusal, and its remedy is a regenerate, not a re-dump.
            if (!hasRecord)
            {
                Refuse(side, "the generated layout header records no game identity for the " + side
                    + " side, so nothing compared " + binaryPath + " (" + binaryIdentity.Format()
                    + ") against the build these values were resolved on. A header without a"
                    + " game: line for a side named on the command line is a check that cannot"
                    + " fire, not a check that passed.", HowToRecord());
                continue;
            }

            if (!was.Equals(binaryIdentity))
            {
                Refuse(side, "the " + side + " GameAssembly.dll on this machine (" + binaryPath
                    + ", " + binaryIdentity.Format() + ") is not the one the generated layout header"
                    + " records (" + was.Format() + "): the game changed under a dump and a header"
                    + " that still describe the old build.", HowToRefresh(side));
            }
        }

        return new DumpFreshness(failures, notes, measured, remedies)
        {
            FailedSides = failedSides,
            Sides = Sorted(dumpPaths.Keys),
            NamedBinaries = Sorted(NonEmptyKeys(binaryPaths)),
        };
    }

    /// <summary>The re-dump procedure, named in every refusal so the reader does not go hunting.</summary>
    public static string HowToRefresh(string side) =>
        "Re-export the game's metadata for that side from the new GameAssembly.dll and its"
        + " global-metadata.dat into <metadata>\\" + side + " (<metadata> = PROGENITOR_METADATA_DIR,"
        + " default <workspace>\\_tools\\interop-dump), re-verify every declaration in"
        + " src/Progenitor.Core/Layout/layout.txt against it, regenerate with tools/layoutgen,"
        + " and run the check again.";

    /// <summary>
    /// What to do when the header records no identity for a side: regenerate it, passing the same
    /// <c>--game-assembly</c> argument the check was given. Named separately from
    /// <see cref="HowToRefresh"/> because the dump here is fine — it is the record that is absent.
    /// </summary>
    /// <remarks>
    /// Deliberately not parameterized by side: the procedure is one generate run naming BOTH
    /// sides, so two sides missing a record must print one instruction, not two. Since job FLAY5 a
    /// generate no longer erases the side it was not given — it carries that side's recorded
    /// identity forward — so naming one side is a way of recording one side rather than a way of
    /// losing the other. Naming both is still the recipe, because it is the only one that records
    /// a side nothing has ever recorded.
    /// </remarks>
    public static string HowToRecord() =>
        "Regenerate the header with --game-assembly so the game: line exists: dotnet run --project"
        + " tools/layoutgen -- --declarations src/Progenitor.Core/Layout/layout.txt --out"
        + " src/Progenitor.Core/Layout/Generated --dump-server <server metadata file> --dump-client"
        + " <client metadata file> --game-assembly server=<server GameAssembly.dll> --game-assembly"
        + " client=<client GameAssembly.dll>. A side you cannot name keeps whatever the header"
        + " already records for it, so name every side that has NO record in ONE run, commit the"
        + " game: line, and run the check again.";

    private static (bool, DateTime, long) FileStat(string path)
    {
        var info = new System.IO.FileInfo(path);
        return info.Exists ? (true, info.LastWriteTimeUtc, info.Length) : (false, default, 0L);
    }

    private static string Stamp(DateTime utc) =>
        utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    private static List<string> Sorted(IEnumerable<string> sides)
    {
        var list = new List<string>(sides);
        list.Sort(StringComparer.Ordinal);
        return list;
    }
}
