using Progenitor.Networking;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The Sample's <c>.request</c> and <c>.subscribe</c> (<c>samples/Progenitor.Sample/SampleExchange.cs</c>): argument
/// parsing, the server's answer and board, and the client's lines. The round trips through Core's engines are in
/// <c>tests/Progenitor.Testing.Tests/Samples/Progenitor.Sample</c>, through <c>ExchangeCompat</c>.
/// </summary>
public class SampleExchangeTests
{
    [Theory]
    [InlineData("", true, 64)]
    [InlineData(" 1 ", true, 1)]
    [InlineData("1000", true, 1000)]
    [InlineData("0", false, 0)]
    [InlineData("1001", false, 0)]
    [InlineData("big", false, 0)]
    public void Request_sizes_parse_within_bounds(string text, bool ok, int kib)
    {
        Assert.Equal(ok, SampleExchange.TryParseKiB(text, out int got));
        if (ok) Assert.Equal(kib, got);
    }

    [Theory]
    [InlineData("", true, "lobby")]
    [InlineData("Chest-7", true, "chest-7")]
    [InlineData("has space", false, "")]
    [InlineData("a_b", false, "")]
    public void Board_keys_parse(string text, bool ok, string key)
    {
        Assert.Equal(ok, SampleExchange.TryParseKey(text, out var got));
        if (ok) Assert.Equal(key, got);
        Assert.False(SampleExchange.TryParseKey(new string('a', 33), out _));
    }

    [Fact]
    public void The_answer_is_deterministic_and_out_of_range_sizes_are_refused()
    {
        var reply = SampleExchangeData.Answer(new BlobRequest { KiB = 3 });
        Assert.Equal(3 * 1024, reply.Data.Length);
        Assert.Equal(SampleExchangeData.Checksum(SampleExchangeData.Blob(3)), reply.Checksum);
        Assert.NotEqual(SampleExchangeData.Checksum(SampleExchangeData.Blob(4)), reply.Checksum);
        var e = Assert.Throws<RequestRefusedException>(() => SampleExchangeData.Answer(new BlobRequest { KiB = 5000 }));
        Assert.Contains("1 to 1000", e.Message);
    }

    [Fact]
    public void The_result_line_checks_the_checksum()
    {
        var reply = SampleExchangeData.Answer(new BlobRequest { KiB = 64 });
        Assert.Equal("request 64 KiB: Ok in 12 ms, 65536 bytes in about 5 packet(s), checksum ok.",
            SampleExchange.ResultLine(64, RequestStatus.Ok, "", reply, 12.3));
        reply.Data[100] ^= 1;
        Assert.EndsWith("checksum MISMATCH.", SampleExchange.ResultLine(64, RequestStatus.Ok, "", reply, 1));
        Assert.Equal("request 2000 KiB: Refused (ask for 1 to 1000 KiB, not 2000) after 5 ms.",
            SampleExchange.ResultLine(2000, RequestStatus.Refused, "ask for 1 to 1000 KiB, not 2000", null, 5));
    }

    [Fact]
    public void A_board_keeps_its_last_lines_and_numbers_its_revisions()
    {
        var board = new SampleExchangeData.Board();
        for (int i = 1; i <= SampleExchangeData.MaxBoardLines + 5; i++) Assert.Equal(i, board.Post("line " + i).Revision);
        var snap = board.Snapshot();
        Assert.Equal(SampleExchangeData.MaxBoardLines, snap.Lines.Count);
        Assert.Equal("line 6", snap.Lines[0]);
        Assert.Equal(SampleExchangeData.SnapshotPaddingBytes, snap.Padding.Length);
        Assert.Equal("board lobby: snapshot rev 55, 50 line(s), 20 KiB padding.", SampleExchange.SnapshotLine("lobby", snap));
        Assert.Equal(SampleExchangeData.MaxLineChars, board.Post(new string('x', 500)).Line.Length);
        Assert.Equal("board lobby: rev 56 'x'.", SampleExchange.DeltaLine("lobby", new BoardLine { Line = "x", Revision = 56 }));
        Assert.Equal("board lobby: subscription ended, Refused (no).", SampleExchange.EndedLine("lobby", SubscriptionEndReason.Refused, "no"));
    }
}
