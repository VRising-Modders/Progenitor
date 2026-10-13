using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Il2CppFakeInterop
{
    public sealed class Wrapper
    {
        [Progenitor.Networking.Field(1)] public int X;
    }
}

namespace Progenitor.Tests
{
    /// <summary>
    /// Mod compatibility C1/C2 review fixes: the merged feature table is capped and only an accepted peer can change it,
    /// the router's log-once set is bounded, deltas never leave rows or removals behind, nested message types are
    /// validated, handler refusals throw, sends are main-thread only, and the raw-send warning settles up front.
    /// </summary>
    public class ModCompatHardeningTests
    {
        private static FeatureTable Table(IEnumerable<string> ids, string version = "1.0")
        {
            var t = new FeatureTable { Core = "c" };
            foreach (var id in ids) t.Mods[id] = new FeatureTableMod(id, version);
            return t;
        }

        private static IEnumerable<string> Ids(string prefix, int n) => Enumerable.Range(0, n).Select(i => prefix + i.ToString("D5"));

        private static void Feed(PeerFeatureBook book, int peer, byte kind, byte[] body, bool accepted, List<PeerFeatureBook.Outcome> outcomes)
        {
            foreach (var p in FeatureTableWire.Pack(kind, body))
            {
                var o = book.Receive(peer, p, out _, accepted);
                if (o != PeerFeatureBook.Outcome.None) outcomes.Add(o);
            }
        }

        // ---- 1. the merged table is capped; an unaccepted peer cannot change it -----------------------------------

        [Fact]
        public void Deltas_of_fresh_ids_cannot_grow_the_merged_table_past_256_mods()
        {
            var book = new PeerFeatureBook();
            book.ApplyFull(0, Table(Ids("base.", 200)));
            var first = book.Get(0);
            for (int round = 0; round < 50; round++)
            {
                var delta = Table(Ids("flood" + round + ".", 256));
                Assert.True(book.ApplyDelta(0, delta, Array.Empty<string>(), out _));
            }

            var t = book.Get(0)!;
            Assert.Equal(ModFeatureLimits.MaxModsPerTable, t.Mods.Count);
            Assert.True(t.More);
            Assert.Same(first, t);   // merged in place: no whole-table copy per delta
        }

        [Fact]
        public void A_delta_still_replaces_and_removes_rows_when_the_table_is_full()
        {
            var book = new PeerFeatureBook();
            book.ApplyFull(0, Table(Ids("m.", 256)));
            var delta = Table(new[] { "m.00000", "new.one" }, "2.0");
            book.ApplyDelta(0, delta, new[] { "m.00001" }, out _);
            var t = book.Get(0)!;
            Assert.Equal("2.0", t.Mods["m.00000"].Version);
            Assert.False(t.Mods.ContainsKey("m.00001"));
            Assert.True(t.Mods.ContainsKey("new.one"));   // the removal made room for it
            Assert.Equal(256, t.Mods.Count);
        }

        [Fact]
        public void An_unaccepted_peer_s_full_table_is_held_its_delta_dropped_and_changed_not_raised()
        {
            var book = new PeerFeatureBook();
            var raised = new List<int>();
            book.Changed += raised.Add;
            var outcomes = new List<PeerFeatureBook.Outcome>();
            Feed(book, 4, FeatureTableWire.KindFull, FeatureTableWire.FullBody(Table(new[] { "a" }), out _), accepted: false, outcomes);
            Feed(book, 4, FeatureTableWire.KindDelta, FeatureTableWire.DeltaBody(Table(Array.Empty<string>()), new[] { new FeatureTableMod("b", "1") }, Array.Empty<string>(), out _), accepted: false, outcomes);
            Assert.Equal(new[] { PeerFeatureBook.Outcome.Held }, outcomes);
            Assert.False(book.Known(4));
            Assert.Empty(raised);

            Assert.True(book.PromotePending(4));
            Assert.True(book.Known(4));
            Assert.Equal(new[] { 4 }, raised);
            Assert.False(book.PromotePending(4));
        }

        [Fact]
        public void Forget_drops_a_held_table()
        {
            var book = new PeerFeatureBook();
            var outcomes = new List<PeerFeatureBook.Outcome>();
            Feed(book, 2, FeatureTableWire.KindFull, FeatureTableWire.FullBody(Table(new[] { "a" }), out _), accepted: false, outcomes);
            book.Forget(2);
            Assert.False(book.PromotePending(2));
        }

        [Fact]
        public void The_server_gates_tables_on_an_ok_handshake_and_promotes_at_ok()
        {
            string net = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModCompatNet.cs");
            Assert.Contains("bool accepted = Handshake.UserAnswerStatus(user.Index) == \"ok\";", net);
            Assert.Contains("PeerTables.Receive(user.Index, new ReadOnlySpan<byte>(payload, length), out string? log, accepted);", net);
            Assert.Contains("PeerTables.PromotePending(user.Index)", net);
            Assert.Contains("if (outcome == PeerFeatureBook.Outcome.Full) LogTable(", net);
        }

        // ---- 2. the router's log-once set is bounded --------------------------------------------------------------

        [Fact]
        public void Random_mod_hashes_cost_a_bounded_set_and_one_summary_line_a_minute()
        {
            double now = 1000;
            var router = new ModMessageRouter { Clock = () => now };
            var lines = new List<string>();
            var rng = new Random(7);
            for (int i = 0; i < 5000; i++)
            {
                byte[] p = ModMessageEnvelope.Pack((uint)rng.Next(), (uint)rng.Next(), 1, 1, new byte[] { 1 });
                router.TryRoute(p, "Player A", null, false, 1, out _, out var log);
                if (log != null) lines.Add(log);
            }

            Assert.Equal(ModMessageRouter.MaxLoggedKeys + 1, lines.Count);
            Assert.Contains("more drop line(s) not logged", lines[^1]);

            now += ModMessageRouter.SuppressedSummarySeconds;
            Assert.NotNull(router.Once("another", "x"));
            Assert.Null(router.Once("and another", "y"));
        }

        [Fact]
        public void A_known_key_still_logs_nothing_after_the_set_is_full()
        {
            var router = new ModMessageRouter { Clock = () => 0 };
            Assert.NotNull(router.Once("k0", "line"));
            for (int i = 1; i < ModMessageRouter.MaxLoggedKeys; i++) router.Once("k" + i, "line");
            Assert.Null(router.Once("k0", "line"));
            router.Clear();
            Assert.NotNull(router.Once("k0", "line"));
        }

        // ---- 3. deltas never leave rows or removals behind --------------------------------------------------------

        [Fact]
        public void A_mod_that_unloads_before_the_first_delta_reaches_peers_as_gone()
        {
            var s = new FeatureDeltaSchedule();
            s.Baseline(Table(Array.Empty<string>()));            // install, before plugins load
            var full = Table(new[] { "a", "b" });                 // a peer's join: its full table
            s.NoteFullSent(full);
            Assert.True(s.Diff(Table(new[] { "a" }), out var changed, out var gone));
            Assert.Empty(changed);
            Assert.Equal(new[] { "b" }, gone);
        }

        [Fact]
        public void A_full_send_does_not_hide_a_change_another_peer_has_not_seen()
        {
            var s = new FeatureDeltaSchedule();
            s.Baseline(Table(new[] { "a" }, "1.0"));              // peer 1 has a 1.0
            var now = Table(new[] { "a" }, "2.0");
            s.NoteFullSent(now);                                    // peer 2 joins and gets a 2.0
            Assert.True(s.Diff(now, out var changed, out _));
            Assert.Equal("2.0", changed.Single().Version);          // peer 1 still learns of it
        }

        [Fact]
        public void Diff_marks_nothing_sent_until_commit()
        {
            var s = new FeatureDeltaSchedule();
            s.Baseline(Table(new[] { "a" }));
            var next = Table(new[] { "a", "b" });
            Assert.True(s.Diff(next, out _, out _));
            Assert.True(s.Diff(next, out var again, out _));
            Assert.Single(again);
            s.Commit(next, 5);
            Assert.False(s.Diff(next, out _, out _));
        }

        [Theory]
        [InlineData(10, 10, 0, true)]
        [InlineData(10, 9, 0, false)]
        [InlineData(0, 0, 256, true)]
        [InlineData(0, 0, 257, false)]
        public void A_cut_delta_is_sent_as_a_full_table(int changed, int kept, int gone, bool whole)
        {
            Assert.Equal(whole, FeatureDeltaSchedule.DeltaIsWhole(changed, kept, gone));
        }

        [Fact]
        public void Over_256_removals_or_a_cut_delta_become_one_full_table_that_matches_the_sender()
        {
            var s = new FeatureDeltaSchedule();
            var before = Table(Ids("x.", 300));
            s.Baseline(before);
            var after = Table(new[] { "keep" });
            Assert.True(s.Diff(after, out var changed, out var gone));
            Assert.Equal(300, gone.Count);
            FeatureTableWire.DeltaBody(after, changed, gone, out int kept);
            Assert.False(FeatureDeltaSchedule.DeltaIsWhole(changed.Count, kept, gone.Count));

            // The fallback: the peer receives the full table and ends up equal to the sender.
            var book = new PeerFeatureBook();
            book.ApplyFull(0, before.Copy());
            var outcomes = new List<PeerFeatureBook.Outcome>();
            Feed(book, 0, FeatureTableWire.KindFull, FeatureTableWire.FullBody(after, out _), true, outcomes);
            Assert.Equal(new[] { "keep" }, book.Get(0)!.Mods.Keys.ToArray());
        }

        [Fact]
        public void The_live_tick_commits_only_whole_deltas_and_baselines_at_install()
        {
            string net = SourcePin.ReadFile("src", "Progenitor.Core", "Networking", "ModCompatNet.cs");
            Assert.Contains("if (!FeatureDeltaSchedule.DeltaIsWhole(changed.Count, kept, gone.Count))", net);
            Assert.Contains("try { Schedule.Baseline(BuildLocalTable()); }", net);
            Assert.Contains("Schedule.NoteFullSent(table);", net);
            Assert.DoesNotContain("Schedule.TakeDelta(", net);
        }

        // ---- 4. nested message types ------------------------------------------------------------------------------

        public sealed class Empty { }
        public sealed class HasEmpty { [Field(1)] public Empty? E; }
        public sealed class HasDictionary { [Field(1)] public Dictionary<string, int> D = new(); }
        public sealed class HasSet { [Field(1)] public HashSet<int> S = new(); }
        public sealed class HasInterop { [Field(1)] public Il2CppFakeInterop.Wrapper? W; }

        [Theory]
        [InlineData(typeof(HasEmpty), "no [Field] members")]
        [InlineData(typeof(HasDictionary), "collection")]
        [InlineData(typeof(HasSet), "collection")]
        [InlineData(typeof(HasInterop), "interop")]
        [InlineData(typeof(Il2CppFakeInterop.Wrapper), "interop")]
        public void Invalid_nested_types_are_refused_with_a_clear_error(Type type, string words)
        {
            var e = Assert.Throws<InvalidOperationException>(() => MessageSchema.For(type));
            Assert.Contains(words, e.Message);
        }

        [Fact]
        public void A_top_level_message_with_no_fields_is_still_allowed()
        {
            Assert.Empty(MessageSchema.For(typeof(Empty)).Encode(new Empty()));
        }

        // ---- 5, 6, 7: refusals throw, sends are main-thread only, the raw warning settles ---------------------------

        [Fact]
        public void A_refused_handler_registration_throws_and_sends_require_the_main_thread()
        {
            string compat = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginCompat.cs");
            Assert.Contains("if (refusal != null) throw new InvalidOperationException(refusal);", compat);
            Assert.DoesNotContain("NopDisposable", compat);
            // Every send overload goes through the guard: the three typed ones via Encode, the three hand-written directly.
            Assert.Equal(4, compat.Split("RequireMainThread();").Length - 1);
            Assert.Contains("if (!Progenitor.Dev.MainThread.IsMainThread)", compat);
        }

        [Fact]
        public void The_raw_warning_is_settled_per_mod_and_id()
        {
            var w = new RawSendWarnings();
            Assert.False(w.Warned("m", 1));
            Assert.NotNull(w.Check("m", 1, "1.0", "1.1", false));
            Assert.True(w.Warned("m", 1));
            Assert.False(w.Warned("m", 2));

            string ctx = SourcePin.ReadFile("src", "Progenitor.Core", "Plugins", "PluginContext.cs");
            Assert.Contains("if (_rawWarningApplies == 0 || CustomEvents.IsReservedEventId(eventId)) return;", ctx);
            Assert.Contains("if (_rawSettled.Contains(eventId)) return;", ctx);
        }
    }
}
