using System;
using System.Collections.Generic;
using System.Text.Json;
using Progenitor.Dev.Ops;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// SnapshotArgs is the debug-bridge <c>snapshot</c> / <c>restore</c> policy: the request
/// parse (including base64 and the size/bytes agreement), the refusal rules that keep a
/// restore from writing the wrong number of bytes into a component, the strict all-or-nothing
/// message, and the result shapes. SnapshotOps applies it against a live entity; these tests
/// cover the parts that do not need the game.
/// </summary>
public class DevSnapshotArgsTests
{
    // ---- snapshot request ---------------------------------------------------------------------

    [Fact]
    public void ParseSnapshot_requires_an_entity()
    {
        var ex = Assert.Throws<ArgumentException>(() => SnapshotArgs.ParseSnapshot(Json("{}")));
        Assert.Contains("args.entity is required", ex.Message);
        Assert.Throws<ArgumentException>(() => SnapshotArgs.ParseSnapshot(default));
    }

    [Fact]
    public void ParseSnapshot_refuses_a_bare_integer_entity()
    {
        var ex = Assert.Throws<ArgumentException>(() => SnapshotArgs.ParseSnapshot(Json("{\"entity\":\"45\"}")));
        Assert.Contains("must be 'index:version'", ex.Message);
    }

    [Fact]
    public void ParseSnapshot_clamps_the_byte_budget()
    {
        Assert.Equal(SnapshotArgs.DefaultMaxBytes, SnapshotArgs.ParseSnapshot(Json("{\"entity\":\"45:1\"}")).MaxBytes);
        Assert.Equal(SnapshotArgs.MinMaxBytes, SnapshotArgs.ParseSnapshot(Json("{\"entity\":\"45:1\",\"max\":1}")).MaxBytes);
        Assert.Equal(SnapshotArgs.MaxMaxBytes, SnapshotArgs.ParseSnapshot(Json("{\"entity\":\"45:1\",\"max\":99999999}")).MaxBytes);
    }

    [Fact]
    public void ParseSnapshot_only_filters_by_simple_or_full_name()
    {
        var r = SnapshotArgs.ParseSnapshot(Json("{\"entity\":\"45:1\",\"only\":[\"Health\"]}"));
        Assert.True(r.Wants("ProjectM.Health"));
        Assert.False(r.Wants("ProjectM.Blood"));
    }

    // ---- restore request ----------------------------------------------------------------------

    [Fact]
    public void ParseRestore_requires_a_snapshot_object()
    {
        Assert.Throws<ArgumentException>(() => SnapshotArgs.ParseRestore(Json("{\"entity\":\"45:1\"}")));
        var ex = Assert.Throws<ArgumentException>(() => SnapshotArgs.ParseRestore(Json("{\"entity\":\"45:1\",\"snapshot\":{}}")));
        Assert.Contains("args.snapshot.components is required", ex.Message);
    }

    [Fact]
    public void ParseRestore_reads_rows_with_bytes_and_defaults_to_strict()
    {
        var r = SnapshotArgs.ParseRestore(Json(Snapshot("{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":\"AAECAw==\"}")));
        Assert.True(r.Strict);
        Assert.Equal("45:1", r.Entity);
        var entry = Assert.Single(r.Entries);
        Assert.Equal("ProjectM.Health", entry.Component);
        Assert.Equal(4, entry.Size);
        Assert.Equal(new byte[] { 0, 1, 2, 3 }, entry.Bytes);
    }

    [Fact]
    public void ParseRestore_honours_strict_false()
    {
        var text = "{\"entity\":\"45:1\",\"strict\":false,\"snapshot\":{\"components\":[{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":\"AAECAw==\"}]}}";
        Assert.False(SnapshotArgs.ParseRestore(Json(text)).Strict);
    }

    [Fact]
    public void ParseRestore_ignores_rows_without_bytes()
    {
        // A whole snapshot result can be handed straight back: tags, buffers and skipped rows
        // carry no blob and are simply not restored, rather than failing the call.
        var rows = "{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":\"AAECAw==\"},"
                   + "{\"component\":\"ProjectM.SomeTag\",\"size\":0},"
                   + "{\"component\":\"ProjectM.BuffBuffer\",\"size\":3,\"bytes\":null}";
        var r = SnapshotArgs.ParseRestore(Json(Snapshot(rows)));
        Assert.Equal("ProjectM.Health", Assert.Single(r.Entries).Component);
    }

    [Fact]
    public void ParseRestore_refuses_a_snapshot_with_no_restorable_rows()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SnapshotArgs.ParseRestore(Json(Snapshot("{\"component\":\"ProjectM.SomeTag\",\"size\":0}"))));
        Assert.Contains("no restorable rows", ex.Message);
    }

    [Fact]
    public void ParseRestore_refuses_bad_base64()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SnapshotArgs.ParseRestore(Json(Snapshot("{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":\"not base64!!\"}"))));
        Assert.Contains("not valid base64", ex.Message);
    }

    [Fact]
    public void ParseRestore_refuses_a_size_that_disagrees_with_the_payload()
    {
        var ex = Assert.Throws<ArgumentException>(
            () => SnapshotArgs.ParseRestore(Json(Snapshot("{\"component\":\"ProjectM.Health\",\"size\":32,\"bytes\":\"AAECAw==\"}"))));
        Assert.Contains("says size 32 but carries 4 decoded bytes", ex.Message);
    }

    [Fact]
    public void ParseRestore_refuses_a_row_without_a_component_name()
    {
        Assert.Throws<ArgumentException>(
            () => SnapshotArgs.ParseRestore(Json(Snapshot("{\"size\":4,\"bytes\":\"AAECAw==\"}"))));
    }

    [Fact]
    public void ParseRestore_refuses_non_string_bytes()
    {
        Assert.Throws<ArgumentException>(
            () => SnapshotArgs.ParseRestore(Json(Snapshot("{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":7}"))));
    }

    // ---- refusal rules ------------------------------------------------------------------------

    [Fact]
    public void RefuseReason_allows_a_matching_unmanaged_component()
    {
        Assert.Null(SnapshotArgs.RefuseReason("ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 32, 32));
    }

    [Fact]
    public void RefuseReason_refuses_a_size_mismatch_in_both_directions()
    {
        var shrunk = SnapshotArgs.RefuseReason("ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 32, 28);
        Assert.NotNull(shrunk);
        Assert.Contains("changed size since the snapshot (32 -> 28 bytes)", shrunk);
        Assert.Contains("corrupt the chunk", shrunk);

        var grown = SnapshotArgs.RefuseReason("ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 32, 40);
        Assert.NotNull(grown);
        Assert.Contains("32 -> 40 bytes", grown);
    }

    [Fact]
    public void RefuseReason_refuses_a_managed_component()
    {
        var reason = SnapshotArgs.RefuseReason("ProjectM.SomeManaged", SnapshotArgs.ComponentKind.Managed, 32, 32);
        Assert.NotNull(reason);
        Assert.Contains("managed component", reason);
        Assert.Contains("no raw blob", reason);
    }

    [Fact]
    public void RefuseReason_refuses_shared_buffer_tag_and_missing()
    {
        Assert.Contains("shared component", SnapshotArgs.RefuseReason("A", SnapshotArgs.ComponentKind.Shared, 8, 8)!);
        Assert.Contains("is a buffer", SnapshotArgs.RefuseReason("B", SnapshotArgs.ComponentKind.Buffer, 8, 8)!);
        Assert.Contains("zero-sized", SnapshotArgs.RefuseReason("C", SnapshotArgs.ComponentKind.ZeroSized, 8, 0)!);
        Assert.Contains("no longer on this entity", SnapshotArgs.RefuseReason("D", SnapshotArgs.ComponentKind.Missing, 8, 0)!);
    }

    [Fact]
    public void RefuseReason_refuses_a_zero_current_size_even_when_the_kind_looks_unmanaged()
    {
        var reason = SnapshotArgs.RefuseReason("ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 32, 0);
        Assert.NotNull(reason);
        Assert.Contains("now reports size 0", reason);
    }

    [Fact]
    public void RefuseReason_refuses_an_empty_recorded_blob()
    {
        var reason = SnapshotArgs.RefuseReason("ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 0, 32);
        Assert.NotNull(reason);
        Assert.Contains("no bytes to write back", reason);
    }

    [Fact]
    public void KindToken_names_every_kind()
    {
        Assert.Equal("unmanaged", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.Unmanaged));
        Assert.Equal("tag", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.ZeroSized));
        Assert.Equal("buffer", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.Buffer));
        Assert.Equal("managed", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.Managed));
        Assert.Equal("shared", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.Shared));
        Assert.Equal("missing", SnapshotArgs.KindToken(SnapshotArgs.ComponentKind.Missing));
    }

    // ---- messages and result shapes -----------------------------------------------------------

    [Fact]
    public void StrictRefusalMessage_says_nothing_was_written_and_lists_every_reason()
    {
        var message = SnapshotArgs.StrictRefusalMessage("45:1", new[] { "first bad", "second bad" });
        Assert.Contains("wrote nothing", message);
        Assert.Contains("2 component(s) failed", message);
        Assert.Contains("first bad", message);
        Assert.Contains("second bad", message);
        Assert.Contains("strict: false", message);
    }

    [Fact]
    public void RestoreLogLine_is_one_line_per_component()
    {
        Assert.Equal("Dev: restore 45:1 ProjectM.Health 32 bytes", SnapshotArgs.RestoreLogLine("45:1", "ProjectM.Health", 32));
    }

    [Fact]
    public void SnapshotRow_omits_bytes_and_fields_for_a_skipped_row()
    {
        var row = SnapshotArgs.SnapshotRow("ProjectM.BuffBuffer", SnapshotArgs.ComponentKind.Buffer, 3, null, null, "buffer");
        Assert.Equal("buffer", row["kind"]);
        Assert.False(row.ContainsKey("bytes"));
        Assert.False(row.ContainsKey("fields"));
        Assert.Equal("buffer", row["skipped"]);
    }

    [Fact]
    public void SnapshotResult_says_it_is_not_a_save()
    {
        var budget = new InspectArgs.Budget(2048);
        budget.TryTake(64);
        var result = SnapshotArgs.SnapshotResult(
            "45:1", "Server", "2026-09-04T00:00:00.0000000Z", "1234-a-b-cafecafecafecafe", 1, 2, budget, null,
            new List<Dictionary<string, object?>>());

        Assert.Equal("45:1", result["entity"]);
        Assert.Equal("Server", result["world"]);
        Assert.Equal("1234-a-b-cafecafecafecafe", result["build"]);
        Assert.Equal(1, result["captured"]);
        Assert.Equal(2, result["skipped"]);
        Assert.Equal(64, result["bytes"]);
        Assert.Equal(false, result["truncated"]);
        Assert.Contains("Not a save", (string)result["note"]!);
    }

    [Fact]
    public void RestoreResult_counts_both_sides()
    {
        var rows = new List<Dictionary<string, object?>>
        {
            SnapshotArgs.RestoreRow("ProjectM.Health", 32, true, null),
            SnapshotArgs.RestoreRow("ProjectM.Blood", 24, false, "changed size"),
        };
        var result = SnapshotArgs.RestoreResult("45:1", true, 1, 1, 0, rows);
        Assert.Equal(1, result["restored"]);
        Assert.Equal(1, result["refused"]);
        Assert.Equal(0, result["warnings"]);
        Assert.Equal(true, result["strict"]);
        Assert.False(rows[0].ContainsKey("reason"));
        Assert.Equal("changed size", rows[1]["reason"]);
    }

    // ---- layout stamp (R30 finding 6) -----------------------------------------------------------

    [Fact]
    public void SnapshotRow_carries_the_type_half_of_the_layout_stamp()
    {
        var stamped = SnapshotArgs.SnapshotRow(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, "AAAA", null, null,
            typeIndex: 77, stableTypeHash: "0x00000000deadbeef");
        Assert.Equal(77, stamped["typeIndex"]);
        Assert.Equal("0x00000000deadbeef", stamped["stableTypeHash"]);

        // A row whose stamp could not be read must not pretend to have one.
        var unstamped = SnapshotArgs.SnapshotRow(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, "AAAA", null, null);
        Assert.False(unstamped.ContainsKey("typeIndex"));
        Assert.False(unstamped.ContainsKey("stableTypeHash"));
    }

    [Fact]
    public void FormatStableTypeHash_is_16_lowercase_hex_digits()
    {
        Assert.Equal("0x00000000deadbeef", SnapshotArgs.FormatStableTypeHash(0xdeadbeefUL));
    }

    // ---- a zero StableTypeHash is "the check could not run" (R31 finding 6) ---------------------

    [Fact]
    public void IsZeroStableTypeHash_spots_the_unknown_value_in_every_wire_form()
    {
        Assert.True(SnapshotArgs.IsZeroStableTypeHash(SnapshotArgs.FormatStableTypeHash(0UL)));
        Assert.True(SnapshotArgs.IsZeroStableTypeHash("0x0000000000000000"));
        Assert.True(SnapshotArgs.IsZeroStableTypeHash(" 0X0 "));
        Assert.True(SnapshotArgs.IsZeroStableTypeHash("0"));

        Assert.False(SnapshotArgs.IsZeroStableTypeHash("0x00000000deadbeef"));
        Assert.False(SnapshotArgs.IsZeroStableTypeHash(null));
        Assert.False(SnapshotArgs.IsZeroStableTypeHash("  "));
    }

    [Fact]
    public void LayoutStamp_treats_a_zero_hash_as_unknown_rather_than_an_identity()
    {
        var stamp = new SnapshotArgs.LayoutStamp("build-a", 77, "0x0000000000000000", 16);
        Assert.Null(stamp.StableTypeHash);
        Assert.False(stamp.IsComplete);
    }

    [Fact]
    public void A_zero_hash_on_both_sides_is_refused_in_strict_mode_not_passed()
    {
        // The whole of the finding: a re-ordered component that keeps its TypeIndex and its byte
        // count. If 0 were formatted as a hash, both sides would read "0x0000000000000000" and
        // the stamp would pass the one case it exists for.
        var recorded = new SnapshotArgs.LayoutStamp("build-a", 77, "0x0000000000000000", 16);
        var live = new SnapshotArgs.LayoutStamp("build-a", 77, "0x0000000000000000", 16);

        var mismatch = SnapshotArgs.LayoutMismatch("ProjectM.Health", recorded, live);
        Assert.NotNull(mismatch);
        Assert.Contains("cannot have its StableTypeHash checked", mismatch);

        SnapshotArgs.DecideRestore(
            "ProjectM.Health",
            SnapshotArgs.ComponentKind.Unmanaged,
            recordedSize: 16,
            currentSize: 16,
            recorded,
            live,
            strict: true,
            out var refusal,
            out var warning);

        Assert.NotNull(refusal);
        Assert.Null(warning);
        Assert.Contains("cannot have its StableTypeHash checked", refusal);
    }

    [Fact]
    public void A_zero_hash_warns_per_row_with_strict_false()
    {
        var recorded = new SnapshotArgs.LayoutStamp("build-a", 77, "0x0000000000000000", 16);
        var live = new SnapshotArgs.LayoutStamp("build-a", 77, "0x0000000000000000", 16);

        SnapshotArgs.DecideRestore(
            "ProjectM.Health",
            SnapshotArgs.ComponentKind.Unmanaged,
            recordedSize: 16,
            currentSize: 16,
            recorded,
            live,
            strict: false,
            out var refusal,
            out var warning);

        Assert.Null(refusal);
        Assert.NotNull(warning);
        Assert.Contains("cannot have its StableTypeHash checked", warning);
    }

    [Fact]
    public void SnapshotRow_records_that_the_hash_could_not_be_read()
    {
        var row = SnapshotArgs.SnapshotRow(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, "AAAA", null, null,
            typeIndex: 77, stableTypeHash: null,
            stableTypeHashUnread: SnapshotArgs.StableTypeHashUnreadReason(readZero: true));

        Assert.False(row.ContainsKey("stableTypeHash"));
        Assert.True(row.ContainsKey(SnapshotArgs.StableTypeHashUnreadKey));
        Assert.Contains("read 0 through this interop", (string?)row[SnapshotArgs.StableTypeHashUnreadKey]);

        // A row that has the hash does not carry the note.
        var ok = SnapshotArgs.SnapshotRow(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, "AAAA", null, null,
            typeIndex: 77, stableTypeHash: "0x00000000deadbeef",
            stableTypeHashUnread: SnapshotArgs.StableTypeHashUnreadReason(readZero: true));
        Assert.False(ok.ContainsKey(SnapshotArgs.StableTypeHashUnreadKey));
    }

    [Fact]
    public void A_snapshot_row_that_recorded_a_zero_hash_is_still_not_trusted_on_restore()
    {
        // An older snapshot (or a hand-written one) can carry the zero string; parsing keeps it,
        // and the stamp drops it.
        var request = SnapshotArgs.ParseRestore(Json(
            "{\"entity\":\"45:1\",\"snapshot\":{\"build\":\"build-a\",\"components\":[" +
            "{\"component\":\"ProjectM.Health\",\"size\":16,\"typeIndex\":77," +
            "\"stableTypeHash\":\"0x0000000000000000\",\"bytes\":\"AAAAAAAAAAAAAAAAAAAAAA==\"}]}}"));

        var entry = Assert.Single(request.Entries);
        Assert.Null(entry.Stamp("build-a").StableTypeHash);
        Assert.False(entry.Stamp("build-a").IsComplete);
    }

    [Fact]
    public void ParseRestore_reads_the_stamp_off_the_snapshot_and_the_row()
    {
        var request = SnapshotArgs.ParseRestore(Json(
            "{\"entity\":\"45:1\",\"snapshot\":{\"build\":\"1-2-3-aaaa\",\"components\":[" +
            "{\"component\":\"ProjectM.Health\",\"size\":4,\"typeIndex\":77,\"stableTypeHash\":\"0x1\",\"bytes\":\"AAAAAA==\"}]}}"));

        Assert.Equal("1-2-3-aaaa", request.Build);
        var entry = Assert.Single(request.Entries);
        Assert.Equal(77, entry.TypeIndex);
        Assert.Equal("0x1", entry.StableTypeHash);
        Assert.Equal("1-2-3-aaaa", entry.Stamp(request.Build).Build);
    }

    [Fact]
    public void ParseRestore_leaves_an_unstamped_snapshot_unstamped()
    {
        var request = SnapshotArgs.ParseRestore(Json(Snapshot(
            "{\"component\":\"ProjectM.Health\",\"size\":4,\"bytes\":\"AAAAAA==\"}")));

        Assert.Null(request.Build);
        var stamp = Assert.Single(request.Entries).Stamp(request.Build);
        Assert.True(stamp.IsAbsent);
        Assert.False(stamp.IsComplete);
    }

    [Fact]
    public void LayoutMismatch_passes_an_identical_stamp()
    {
        var stamp = new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16);
        Assert.Null(SnapshotArgs.LayoutMismatch("ProjectM.Health", stamp, stamp));
    }

    [Fact]
    public void LayoutMismatch_names_the_build_when_the_game_binary_changed()
    {
        var mismatch = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-b", 77, "0x1", 16));

        Assert.NotNull(mismatch);
        Assert.Contains("game build 'build-a'", mismatch);
        Assert.Contains("build 'build-b'", mismatch);
        Assert.DoesNotContain("StableTypeHash", mismatch);
    }

    [Fact]
    public void LayoutMismatch_names_the_stable_hash_when_only_the_shape_changed()
    {
        // The finding's exact case: same size, same build, different layout.
        var mismatch = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x2", 16));

        Assert.NotNull(mismatch);
        Assert.Contains("StableTypeHash changed", mismatch);
        Assert.Contains("0x1 -> 0x2", mismatch);
    }

    [Fact]
    public void LayoutMismatch_names_the_type_index_when_the_name_resolves_elsewhere()
    {
        var mismatch = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-a", 78, "0x1", 16));

        Assert.NotNull(mismatch);
        Assert.Contains("TypeIndex 77", mismatch);
        Assert.Contains("78", mismatch);
    }

    [Fact]
    public void LayoutMismatch_reports_a_check_that_could_not_run_rather_than_passing_it()
    {
        var live = new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16);

        var unstamped = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health", new SnapshotArgs.LayoutStamp(null, -1, null, 16), live);
        Assert.Contains("no layout stamp in the snapshot", unstamped);

        var noLiveBuild = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp(null, 77, "0x1", 16));
        Assert.Contains("this process does not report a GameAssembly build id", noLiveBuild);

        var noLiveHash = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-a", 77, null, 16));
        Assert.Contains("StableTypeHash checked", noLiveHash);
    }

    [Fact]
    public void LayoutRefusal_is_overridable_only_while_the_sizes_agree()
    {
        var mismatch = SnapshotArgs.LayoutMismatch(
            "ProjectM.Health",
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-b", 77, "0x1", 16))!;

        var overridable = SnapshotArgs.LayoutRefusal(mismatch, sizesMatch: true);
        Assert.Contains("Pass strict: false", overridable);
        Assert.Contains("nothing here checks the field order", overridable);

        var final = SnapshotArgs.LayoutRefusal(mismatch, sizesMatch: false);
        Assert.Contains("cannot be overridden", final);
        Assert.DoesNotContain("Pass strict: false", final);
    }

    [Fact]
    public void LayoutWarning_says_what_was_not_verified()
    {
        var warning = SnapshotArgs.LayoutWarning("'ProjectM.Health' StableTypeHash changed");
        Assert.Contains("strict is false", warning);
        Assert.Contains("field order was not verified", warning);
    }

    [Fact]
    public void DecideRestore_refuses_a_same_size_stamp_change_in_strict_mode()
    {
        // R30 finding 6: 16 bytes before, 16 bytes after, fields re-ordered by a game patch.
        SnapshotArgs.DecideRestore(
            "ProjectM.Health",
            SnapshotArgs.ComponentKind.Unmanaged,
            recordedSize: 16,
            currentSize: 16,
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-b", 77, "0x9", 16),
            strict: true,
            out var refusal,
            out var warning);

        Assert.NotNull(refusal);
        Assert.Null(warning);
        Assert.Contains("game build 'build-a'", refusal);
        Assert.Contains("Pass strict: false", refusal);
    }

    [Fact]
    public void DecideRestore_allows_a_same_size_stamp_change_with_strict_false_and_warns()
    {
        SnapshotArgs.DecideRestore(
            "ProjectM.Health",
            SnapshotArgs.ComponentKind.Unmanaged,
            recordedSize: 16,
            currentSize: 16,
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            new SnapshotArgs.LayoutStamp("build-b", 77, "0x9", 16),
            strict: false,
            out var refusal,
            out var warning);

        Assert.Null(refusal);
        Assert.NotNull(warning);
        Assert.Contains("field order was not verified", warning);
    }

    [Fact]
    public void DecideRestore_keeps_the_size_rules_hard_in_both_modes()
    {
        var recorded = new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16);
        var live = new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 32);

        foreach (var strict in new[] { true, false })
        {
            SnapshotArgs.DecideRestore(
                "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, 32,
                recorded, live, strict, out var refusal, out var warning);

            Assert.Null(warning);
            Assert.NotNull(refusal);
            Assert.Contains("changed size since the snapshot", refusal);
        }
    }

    [Fact]
    public void DecideRestore_is_silent_when_the_stamp_matches()
    {
        var stamp = new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16);
        SnapshotArgs.DecideRestore(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, 16,
            stamp, stamp, strict: true, out var refusal, out var warning);

        Assert.Null(refusal);
        Assert.Null(warning);
    }

    [Fact]
    public void DecideRestore_refuses_an_unstamped_snapshot_in_strict_mode()
    {
        SnapshotArgs.DecideRestore(
            "ProjectM.Health", SnapshotArgs.ComponentKind.Unmanaged, 16, 16,
            new SnapshotArgs.LayoutStamp(null, -1, null, 16),
            new SnapshotArgs.LayoutStamp("build-a", 77, "0x1", 16),
            strict: true,
            out var refusal,
            out var warning);

        Assert.Null(warning);
        Assert.Contains("no layout stamp in the snapshot", refusal);
    }

    [Fact]
    public void RestoreRow_carries_a_warning_only_when_one_was_given()
    {
        var warned = SnapshotArgs.RestoreRow("ProjectM.Health", 16, true, null, "wrote it anyway");
        Assert.Equal("wrote it anyway", warned["warning"]);
        Assert.True((bool)warned["restored"]!);

        var clean = SnapshotArgs.RestoreRow("ProjectM.Health", 16, true, null);
        Assert.False(clean.ContainsKey("warning"));
    }

    private static string Snapshot(string rows) =>
        "{\"entity\":\"45:1\",\"snapshot\":{\"components\":[" + rows + "]}}";

    private static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;
}
