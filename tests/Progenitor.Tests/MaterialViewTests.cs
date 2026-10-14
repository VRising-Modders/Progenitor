using System;
using System.Linq;
using Progenitor.Castles;
using Progenitor.Materials;
using Xunit;

namespace Progenitor.Tests;

public class MaterialViewTests
{
    private static MaterialView View(uint seq, int epoch = 3, bool cleared = false, params (int, int)[] rows) =>
        new(MaterialOperation.Craft, epoch, 42, seq, 5, cleared, false,
            rows.Select(r => new MaterialLine(r.Item1, r.Item2)).ToArray(), 0x0000000700000001UL, 0x0000000900000002UL, 5);

    [Fact]
    public void Wire_round_trips()
    {
        var view = View(9, 3, false, (100, 12), (200, 0));
        Assert.True(MaterialViewWire.TryDecode(MaterialViewWire.Encode(view), out var back, out _));
        Assert.Equal(MaterialOperation.Craft, back!.Operation);
        Assert.Equal(3, back.Epoch);
        Assert.Equal(42, back.Revision);
        Assert.Equal(9u, back.Sequence);
        Assert.Equal(5, back.Territory);
        Assert.Equal(0x0000000700000001UL, back.HeartNet);
        Assert.Equal(0x0000000900000002UL, back.StationNet);
        Assert.Equal(5u, back.Generation);
        Assert.Equal(new[] { new MaterialLine(100, 12), new MaterialLine(200, 0) }, back.Totals);
    }

    [Fact]
    public void Wire_stays_under_the_packet_cap_and_flags_truncation()
    {
        var rows = Enumerable.Range(1, MaterialViewWire.MaxRows + 10).Select(i => (i, 1)).ToArray();
        var bytes = MaterialViewWire.Encode(View(1, 3, false, rows));
        Assert.True(bytes.Length <= 15 * 1024);
        Assert.True(MaterialViewWire.TryDecode(bytes, out var back, out _));
        Assert.True(back!.Truncated);
        Assert.Equal(MaterialViewWire.MaxRows, back.Totals.Count);
    }

    [Fact]
    public void Wire_refuses_malformed_packets()
    {
        var bytes = MaterialViewWire.Encode(View(1, 3, false, (1, 1)));
        Assert.False(MaterialViewWire.TryDecode(bytes.AsSpan(0, 10), out _, out _));
        Assert.False(MaterialViewWire.TryDecode(bytes.AsSpan(0, bytes.Length - 1), out _, out _));
        var bad = (byte[])bytes.Clone();
        bad[0] = 99;
        Assert.False(MaterialViewWire.TryDecode(bad, out _, out var why));
        Assert.Contains("version", why);
        var v1 = (byte[])bytes.Clone();
        v1[0] = 1; // the spike's format listed ungoverned items too; an old packet shows the game's own counts
        Assert.False(MaterialViewWire.TryDecode(v1, out _, out _));
        Assert.Equal(0x50724D76, MaterialViewWire.EventId);
        Assert.Equal("PrMv", new string(new[] { (char)(MaterialViewWire.EventId >> 24), (char)((MaterialViewWire.EventId >> 16) & 0xFF), (char)((MaterialViewWire.EventId >> 8) & 0xFF), (char)(MaterialViewWire.EventId & 0xFF) }));
    }

    [Fact]
    public void Overlay_keeps_the_newest_sequence_and_resets_on_a_new_epoch()
    {
        var store = new MaterialOverlayStore();
        Assert.True(store.Apply(View(2, 3, false, (100, 5))));
        Assert.False(store.Apply(View(1, 3, false, (100, 99))));
        Assert.True(store.TryGet(MaterialOperation.Craft, out var t));
        Assert.Equal(5, t[100]);
        Assert.True(store.Apply(View(1, 4, false, (100, 7)))); // new world epoch: sequence restarts
        Assert.True(store.TryGet(MaterialOperation.Craft, out t));
        Assert.Equal(7, t[100]);
        Assert.False(store.TryGet(MaterialOperation.Build, out _));
    }

    [Fact]
    public void A_cleared_view_returns_the_client_to_the_games_counts()
    {
        var store = new MaterialOverlayStore();
        store.Apply(View(1, 3, false, (100, 5)));
        store.Apply(View(2, 3, true));
        Assert.False(store.TryGet(MaterialOperation.Craft, out _));
        store.Apply(View(3, 3, false, (100, 1)));
        store.Clear();
        Assert.Equal(0, store.Count);
    }

    [Fact]
    public void View_book_sends_only_changes_and_numbers_them()
    {
        var book = new MaterialViewBook();
        var user = new CastleId(9, 1);
        ulong f1 = MaterialViewBook.Fingerprint(5, 1, new[] { new MaterialLine(100, 5) });
        ulong f2 = MaterialViewBook.Fingerprint(5, 2, new[] { new MaterialLine(100, 5) });
        Assert.NotEqual(f1, f2);
        Assert.True(book.ShouldSend(user, MaterialOperation.Craft, f1, false, out uint s1));
        Assert.False(book.ShouldSend(user, MaterialOperation.Craft, f1, false, out _));
        Assert.True(book.ShouldSend(user, MaterialOperation.Craft, f1, true, out uint s2));
        Assert.True(book.ShouldSend(user, MaterialOperation.Build, f1, false, out uint s3));
        Assert.True(s1 < s2 && s2 < s3);
        book.Forget(user);
        Assert.True(book.ShouldSend(user, MaterialOperation.Craft, f2, false, out uint s4));
        Assert.True(s4 > s3); // a reconnect never goes backwards
    }

    [Fact]
    public void Failed_clear_is_retried_until_accepted()
    {
        var book = new MaterialViewBook();
        var user = new CastleId(9, 1);
        Assert.True(book.ShouldSend(user, MaterialOperation.Build, 123, false, out _));
        Assert.True(book.ShouldSend(user, MaterialOperation.Build, MaterialViewBook.ClearedFingerprint, false, out var first));
        book.Retry(user, MaterialOperation.Build);
        Assert.True(book.HasView(user, MaterialOperation.Build));
        Assert.True(book.ShouldSend(user, MaterialOperation.Build, MaterialViewBook.ClearedFingerprint, false, out var retry));
        Assert.True(retry > first);
        Assert.False(book.HasView(user, MaterialOperation.Build));
        Assert.False(book.ShouldSend(user, MaterialOperation.Build, MaterialViewBook.ClearedFingerprint, false, out _));
    }

    [Fact]
    public void Scope_governs_only_inside_the_frame_it_was_opened_in()
    {
        var scope = new MaterialScope();
        Assert.False(scope.TryCurrent(10, out _));
        long outer = scope.Enter(MaterialOperation.Build, 10);
        long inner = scope.Enter(MaterialOperation.Craft, 10);
        Assert.True(scope.TryCurrent(10, out var op));
        Assert.Equal(MaterialOperation.Craft, op);
        scope.Exit(inner);
        Assert.True(scope.TryCurrent(10, out op));
        Assert.Equal(MaterialOperation.Build, op);
        // The system threw: the closing hook never ran. The next frame sees no scope.
        Assert.False(scope.TryCurrent(11, out _));
        Assert.Equal(0, scope.Depth);
        scope.Exit(outer); // late close of a dropped scope is harmless
        long next = scope.Enter(MaterialOperation.Craft, 11);
        Assert.True(scope.TryCurrent(11, out op));
        scope.Exit(next);
        Assert.False(scope.TryCurrent(11, out _));
    }
}
