using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FEX5 (RD6 J13). The Sample's registry-read-back example.
///
/// The registry calls bind <c>Stunlock.Core</c> and <c>Unity.Entities</c> and are behind
/// <c>!PROGENITOR_TESTS</c>, so this pins the parse, every reply line, and the two decisions that
/// would be silent if they regressed: that the second item is registered under its <b>own</b>
/// owner (registering it under <c>ctx.Owner</c> would make <c>.content drop</c> take the Ember
/// Shard and the buff with it, and the drop would still look successful), and that the read-back
/// line reports an inequality rather than only printing two numbers.
/// </summary>
public class SampleContentTests
{
    // The expectation travels as an int because SampleContent.Kind is internal and an xUnit
    // theory method has to be public.
    [Theory]
    [InlineData("", 0)]
    [InlineData("   ", 0)]
    [InlineData("status", 0)]
    [InlineData("DEFINE", 1)]
    [InlineData("Drop", 2)]
    public void TryParse_accepts_the_three_forms(string token, int expected)
    {
        Assert.True(SampleContent.TryParse(token, out var kind));
        Assert.Equal((SampleContent.Kind)expected, kind);
    }

    [Theory]
    [InlineData("unregister")]
    [InlineData("define sample.cinder")]
    [InlineData("remove")]
    public void TryParse_refuses_anything_else(string token)
    {
        Assert.False(SampleContent.TryParse(token, out _));
    }

    /// <summary>
    /// The whole point of the example. <c>UnregisterAll</c> takes an owner, and the Sample has
    /// three registrations under <c>ctx.Owner</c> (the item, the buff, and this one would be the
    /// third) — so a drop keyed on <c>ctx.Owner</c> would retire all of them and report success.
    /// </summary>
    [Fact]
    public void The_second_item_is_registered_under_its_own_owner_and_not_the_plugin()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "var owner = new CinderOwner();");
        SourcePin.AssertStatement(src, "int count = PrefabRegistry.UnregisterAll(owner);");
        Assert.DoesNotContain("UnregisterAll(ctx.Owner)", src, StringComparison.Ordinal);
        Assert.DoesNotContain("UnregisterAll(plugin.Owner)", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// <c>Register(owner)</c> is the static overload that takes an owner;
    /// <c>ctx.Content.DefineItem(...)</c>'s builder uses the plugin and has no owner argument, so
    /// swapping to it would silently put the definition back under <c>ctx.Owner</c>.
    /// </summary>
    [Fact]
    public void The_define_goes_through_the_owner_taking_registry_overload()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "var definition = PrefabRegistry.DefineItem(CinderId)");
        SourcePin.AssertStatement(src, ".Register(owner);");
    }

    [Fact]
    public void The_editor_reads_the_guid_and_never_retargets_it()
    {
        string src = Source();
        SourcePin.AssertStatement(src, "if (!prefab.TryRead<PrefabGUID>(out var guid))");
        // The identity write the registry refuses anyway; the example must not model it.
        SourcePin.AssertNoStatementContaining(src, "prefab.Write(in guid");
        // SetComponent<T> is the ItemData path (bool fields, not C# unmanaged); this editor's
        // component is unmanaged, so it takes the typed Write<T> route instead.
        SourcePin.AssertNoStatementContaining(src, "prefab.SetComponent");
    }

    /// <summary>
    /// The four calls the api.md row names, in the order a prefab edit needs them:
    /// <c>Has&lt;T&gt;</c> (Write requires the component to already be there), <c>TryRead&lt;T&gt;</c>
    /// (get the vanilla value), <c>Write&lt;T&gt;(in value)</c> (typed, so it is a
    /// <c>SetComponentDataRaw</c> of <c>sizeof(T)</c>), <c>Read&lt;T&gt;</c> (prove it stuck).
    /// </summary>
    [Fact]
    public void The_editor_guards_the_typed_write_with_Has_and_TryRead_and_reads_it_back()
    {
        string src = Source();
        // The component the live clone actually carries (evidence 212). Salvageable, which the
        // first draft used, is not on it, so Has<T> read false and this half never ran.
        SourcePin.AssertStatement(src, "if (!prefab.Has<DestroyData>() || !prefab.TryRead<DestroyData>(out var destroy))");
        SourcePin.AssertStatementExactly(src, "prefab.Write(in destroy);");
        SourcePin.AssertStatementExactly(src, "byte after = (byte)prefab.Read<DestroyData>().DestroyReason;");
        // The source still names Salvageable in prose (why the component changed), so the pin is
        // on the call and not on the word.
        SourcePin.AssertNoStatementContaining(src, "prefab.Has<Salvageable>");
    }

    /// <summary>
    /// FEDIT1's contract is the reason this write is safe to demonstrate: Core captured the
    /// pre-write value and restores it when the editor is removed. An example that undid its own
    /// edit by hand would be modeling the bug that contract removes, and would hide whether the
    /// restore works.
    /// </summary>
    [Fact]
    public void Nothing_in_the_sample_undoes_the_edit_by_hand()
    {
        string src = Source();
        SourcePin.AssertNoStatementContaining(src, "destroy.DestroyReason = before");
        SourcePin.AssertNoStatementContaining(src, "DestroyReason = original");
        Assert.Contains("restores it", SampleContent.DestroyWriteLine(0, 1), StringComparison.Ordinal);
    }

    [Fact]
    public void The_write_line_carries_both_numbers_and_says_the_length_is_sizeof()
    {
        string line = SampleContent.DestroyWriteLine(0, SampleContent.DestroyReasonWritten);

        Assert.Contains("DestroyReason Default(0) -> Consume(2)", line, StringComparison.Ordinal);
        Assert.Contains("sizeof(T)", line, StringComparison.Ordinal);
        Assert.Contains("never a caller-chosen byte count", line, StringComparison.Ordinal);
    }

    [Fact]
    public void The_write_read_back_line_says_which_way_the_comparison_went()
    {
        Assert.Contains("equal to what Write<T> was given", SampleContent.DestroyReadBackLine(2, 2), StringComparison.Ordinal);
        Assert.Contains("DIFFERENT", SampleContent.DestroyReadBackLine(2, 0), StringComparison.Ordinal);
        Assert.DoesNotContain("DIFFERENT", SampleContent.DestroyReadBackLine(2, 2), StringComparison.Ordinal);
    }

    /// <summary>
    /// The names come from <c>DestroyReason</c> in the server metadata and are the readable
    /// half of the before/after line; the number is always printed beside them so a name table
    /// that goes stale after a game update cannot hide what was actually written.
    /// </summary>
    [Theory]
    [InlineData(0, "Default")]
    [InlineData(2, "Consume")]
    [InlineData(13, "OnHitConsume")]
    [InlineData(200, "unknown")]
    public void The_destroy_reason_names_match_the_dump(int value, string expected)
    {
        Assert.Equal(expected, SampleContent.DestroyReasonName((byte)value));
        Assert.Contains("(" + value + ")", SampleContent.DestroyWriteLine(0, (byte)value), StringComparison.Ordinal);
    }

    /// <summary>
    /// A missing component is not an error and does not become an <c>Add</c>: <c>Write&lt;T&gt;</c>
    /// edits, it does not create, and adding a component to a prefab is a structural change with
    /// different consequences.
    /// </summary>
    [Fact]
    public void A_prefab_without_the_component_is_skipped_and_the_line_says_why()
    {
        Assert.Contains("Has<ProjectM.DestroyData>() is false", SampleContent.DestroyMissingLine, StringComparison.Ordinal);
        Assert.Contains("Entity.Add<T>", SampleContent.DestroyMissingLine, StringComparison.Ordinal);
        SourcePin.AssertNoStatementContaining(Source(), "prefab.Add<");
    }

    [Fact]
    public void The_read_back_line_says_which_way_the_comparison_went()
    {
        Assert.Contains("equal", SampleContent.ReadBackLine(42, 42), StringComparison.Ordinal);
        Assert.Contains("DIFFERENT", SampleContent.ReadBackLine(42, 43), StringComparison.Ordinal);
        Assert.DoesNotContain("DIFFERENT", SampleContent.ReadBackLine(42, 42), StringComparison.Ordinal);
    }

    [Fact]
    public void Status_prints_the_registry_summary_verbatim_because_it_carries_the_tombstone_count()
    {
        var lines = SampleContent.StatusLines("definitions=3 tombstones=1 appended=2", null, cinderDefined: false);
        Assert.Contains(lines, l => l.Contains("definitions=3 tombstones=1 appended=2", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_lists_the_rows_it_is_given_and_says_how_many_it_cut()
    {
        var rows = new List<string>();
        for (int i = 0; i < SampleContent.MaxRowsListed + 4; i++)
            rows.Add(SampleContent.DefinitionLine("id" + i, i, -i, "Item", "Sample"));

        var lines = SampleContent.StatusLines("definitions=16 tombstones=0", rows, cinderDefined: true);
        Assert.Contains(lines, l => l.Contains("id0 guid=0", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, l => l.Contains("id" + (SampleContent.MaxRowsListed + 3) + " ", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.Contains("… and 4 more", StringComparison.Ordinal));
    }

    [Fact]
    public void Status_says_which_of_the_two_next_steps_applies()
    {
        Assert.Contains(
            SampleContent.StatusLines("x", null, cinderDefined: true),
            l => l.Contains(".content drop", StringComparison.Ordinal));
        Assert.Contains(
            SampleContent.StatusLines("x", null, cinderDefined: false),
            l => l.Contains(".content define", StringComparison.Ordinal));
    }

    [Fact]
    public void An_empty_registry_is_reported_rather_than_printing_an_empty_list_header()
    {
        var lines = SampleContent.StatusLines("definitions=0 tombstones=0", Array.Empty<string>(), false);
        Assert.Contains(lines, l => l.Contains("Definitions is empty", StringComparison.Ordinal));
    }

    /// <summary>
    /// "Dropped" must not read as "deleted": the clone stays resolvable and the key row stays as
    /// a tombstone forever, and an operator who believes otherwise will go looking for a row that
    /// is never coming back.
    /// </summary>
    [Fact]
    public void The_drop_reply_says_soft_unapply_and_names_both_packets()
    {
        string reply = SampleContent.DroppedReply(1);
        Assert.Contains("soft unapply", reply, StringComparison.Ordinal);
        Assert.Contains("PrfU", reply, StringComparison.Ordinal);
        Assert.Contains("PrfK", reply, StringComparison.Ordinal);
        Assert.Contains("never shrunk", reply, StringComparison.Ordinal);
    }

    [Fact]
    public void Define_and_drop_refuse_the_wrong_order_with_a_reply_that_says_what_to_do()
    {
        Assert.Contains(".content drop", SampleContent.AlreadyDefinedReply(), StringComparison.Ordinal);
        Assert.Contains(".content define", SampleContent.NotDefinedReply(), StringComparison.Ordinal);
    }

    [Fact]
    public void The_definition_row_carries_the_id_guid_source_kind_and_owner()
    {
        string row = SampleContent.DefinitionLine("sample.ember", 111, -257494203, "Item", "Progenitor Sample");
        Assert.Contains("sample.ember", row, StringComparison.Ordinal);
        Assert.Contains("guid=111", row, StringComparison.Ordinal);
        Assert.Contains("source=-257494203", row, StringComparison.Ordinal);
        Assert.Contains("kind=Item", row, StringComparison.Ordinal);
        Assert.Contains("owner=Progenitor Sample", row, StringComparison.Ordinal);
    }

    private static string Source()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = Path.Combine(dir.FullName, "samples", "Progenitor.Sample", "SampleContent.cs");
            if (File.Exists(candidate))
                return File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new FileNotFoundException("samples/Progenitor.Sample/SampleContent.cs not found from " + AppContext.BaseDirectory);
    }

    /// <summary>
    /// R65 finding 3. FSAMP3's three metadata citations did not point at what they named, and the
    /// safety claim beside them was the wrong one. Every citation pinned here was checked against
    /// the server metadata while the fix was written:
    /// <c>TagNewDestroyedEntity</c> is a <b>private</b> method of <c>ProcessDestroyEventSystem</c>
    /// (the only declaration of that name) and not a member of <c>DestroyUtility</c>;
    /// <c>DestroyReason.Consume</c> and <c>DestroyStateEnum.DestroyNow = 2</c> are cited at their
    /// own members, not at a neighbor or at the <c>DestroyState</c> struct. And the write is not
    /// safe because nothing reads the value —
    /// <c>DropInventorySystem</c> and <c>WeaponCoatingSourceDestroySystem</c>
    /// both branch on <c>DestroyData</c> — but because both sit in
    /// <c>ReactToDestroyTagGroup</c>, which is
    /// <c>[UpdateAfter(typeof(DestroyTagGroup))]</c>, after the
    /// <c>OrderLast</c> destroy system that re-stamps the reason.
    ///
    /// <para>The pinned text <em>is</em> a comment, which is the one case
    /// <see cref="SourcePin"/> says <c>Assert.Contains</c> over source is for.</para>
    /// </summary>
    [Fact]
    public void The_dump_citations_and_the_safety_claim_match_the_dump()
    {
        string src = Source();

        Assert.DoesNotContain("DestroyUtility.TagNewDestroyedEntity", src, StringComparison.Ordinal);
        // Citations name the member itself, never a neighbor or the containing struct.
        Assert.Contains("DestroyReason.Consume", src, StringComparison.Ordinal);
        Assert.Contains("DestroyStateEnum.DestroyNow = 2", src, StringComparison.Ordinal);
        Assert.Contains("ProcessDestroyEventSystem", src, StringComparison.Ordinal);

        // The claim that replaced "only ever read as an annotation".
        Assert.DoesNotContain("only ever read as an annotation", src, StringComparison.Ordinal);
        Assert.Contains("DropInventorySystem", src, StringComparison.Ordinal);
        Assert.Contains("WeaponCoatingSourceDestroySystem", src, StringComparison.Ordinal);
        Assert.Contains("UpdateAfter(typeof(DestroyTagGroup))", src, StringComparison.Ordinal);
        Assert.Contains("OrderLast", src, StringComparison.Ordinal);

        // And the half of it the metadata cannot settle is labeled as such rather than asserted.
        Assert.Contains("Burst-compiled and its native code was not read", src, StringComparison.Ordinal);
    }

    /// <summary>
    /// R66 finding 9. R65 finding 3 fixed the citations; the claim they were fixed to support was
    /// still one step longer than the citations reach. The unverified step —
    /// <c>TagNewDestroyedEntity</c> writing <c>DestroyData</c> (its dump body at <c>:38479</c> is
    /// <c>{ }</c>) — is the <em>whole</em> argument that an instance's copied <c>Consume</c> never
    /// reaches <c>DropInventorySystem</c> and <c>WeaponCoatingSourceDestroySystem</c>: without it
    /// the group ordering is irrelevant and the template's value arrives untouched. So the text
    /// must not say the argument survives the disclosure ("holds either way, because it is what
    /// the two readers depend on"), and the safety claim the example actually makes must rest on
    /// the one thing that needs no ordering argument at all — the edited entity is a
    /// <c>Prefab</c> template that is never destroyed and so is never resolved by either reader.
    ///
    /// <para>Pinned as text because the claim <em>is</em> a comment, which is the case
    /// <see cref="SourcePin"/> reserves <c>Assert.Contains</c> over source for. Each fragment sits
    /// on one line of the doc comment, so a re-wrap that changes the words fails rather than a
    /// re-wrap that only moves them.</para>
    /// </summary>
    [Fact]
    public void The_instance_safety_argument_is_not_claimed_to_survive_its_own_disclosure()
    {
        string src = Source();

        // The sentence R66 finding 9 is against, and the conclusion it propped up.
        Assert.DoesNotContain("because it is what the two readers depend on", src, StringComparison.Ordinal);
        Assert.DoesNotContain("the value written here never reaches them", src, StringComparison.Ordinal);

        // The instance half is stated as conditional on the inferred write ...
        Assert.Contains("So <em>if</em> that method is what writes", src, StringComparison.Ordinal);
        // ... and the disclosure names what fails if that step is wrong.
        Assert.Contains("the argument about instances does not", src, StringComparison.Ordinal);
        Assert.Contains("every instance carries <c>Consume</c> into", src, StringComparison.Ordinal);

        // The safety the example does claim needs no ordering argument.
        Assert.Contains("never-destroyed <c>Prefab</c> template", src, StringComparison.Ordinal);
    }

    /// <summary>The live server refused the first draft (79 bytes) with "localized description must be 1-63 ASCII bytes"; the packet slot is PrefabWire.AsciiCapacity - 1.</summary>
    [Fact]
    public void The_cinder_name_and_description_fit_the_definition_packet()
    {
        Assert.InRange(System.Text.Encoding.ASCII.GetByteCount(Progenitor.Sample.SampleContent.CinderName), 1, Progenitor.Content.PrefabWire.AsciiCapacity - 1);
        Assert.InRange(System.Text.Encoding.ASCII.GetByteCount(Progenitor.Sample.SampleContent.CinderDescription), 1, Progenitor.Content.PrefabWire.AsciiCapacity - 1);
    }
}
