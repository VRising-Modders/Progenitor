using System;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// REASK1 (live 2026-10-07): Core hot-swapped on the test server and the client at the same moment; the server re-asked
/// the connected player, the client (mid swap) never answered, and the player who had joined fine was kicked with the
/// "install the Progenitor mod" message. A re-ask of a user who answered this session must never disconnect them.
/// </summary>
public sealed class HandshakeReaskTests
{
    private const ulong Steam = 76561198000000042UL;

    /// <summary>A process slot shared by two "generations" (two memory instances over one string).</summary>
    private sealed class Slot
    {
        public string? Value;
        public HandshakeSessionMemory Generation() => new(() => Value, v => Value = v);
    }

    /// <summary>
    /// The soft re-ask's whole run against a client that answers on <paramref name="answersAtSend"/> (1-based; 0 never):
    /// the steps taken. A step list has no kick: the enum has none, and the run ends answered or kept connected.
    /// </summary>
    private static (int Sends, HandshakeReaskStep Last) Run(int answersAtSend, bool stillConnected = true)
    {
        int sends = 0;
        for (int attempt = 0; ; attempt++)
        {
            sends++;
            bool answered = answersAtSend > 0 && sends >= answersAtSend;
            var step = HandshakeReask.After(attempt, answered, stillConnected);
            if (step != HandshakeReaskStep.Resend) return (sends, step);
        }
    }

    [Fact]
    public void Simultaneous_swaps_the_server_remembers_and_the_client_answers_after_its_own_swap()
    {
        var slot = new Slot();
        // Generation 1: the player joined and answered.
        slot.Generation().Remember(3, Steam);
        // Generation 2 (the server's Core swap): a fresh Core reads the same process slot.
        var g2 = slot.Generation();
        Assert.True(g2.Contains(3, Steam));
        Assert.Equal(HandshakeAskKind.SoftReask, HandshakeReask.KindFor(reask: true, rememberedAnswered: g2.Contains(3, Steam)));

        // The client was swapping too: the first request is lost. Its refresh after its swap arrives while the soft
        // re-ask waits (status not ok in the new server generation) and is answered: the request is sent again.
        Assert.True(HandshakeReask.RefreshAllowed(handshakeOk: false, softReaskPending: true));
        Assert.False(HandshakeReask.RefreshAllowed(handshakeOk: false, softReaskPending: false));
        var run = Run(answersAtSend: 2);
        Assert.Equal(HandshakeReaskStep.Done, run.Last);
        Assert.Equal(2, run.Sends);
    }

    [Fact]
    public void A_client_swapping_while_the_server_re_asks_is_resent_the_request_and_never_kicked()
    {
        var run = Run(answersAtSend: 3);
        Assert.Equal(HandshakeReaskStep.Done, run.Last);
        Assert.Equal(new[] { HandshakeReaskStep.Done, HandshakeReaskStep.Resend, HandshakeReaskStep.KeepConnected },
            Enum.GetValues(typeof(HandshakeReaskStep)).Cast<HandshakeReaskStep>().ToArray());
    }

    [Fact]
    public void A_server_swap_with_no_client_answer_keeps_the_user_connected_and_tells_the_admins()
    {
        var run = Run(answersAtSend: 0);
        Assert.Equal(HandshakeReaskStep.KeepConnected, run.Last);
        Assert.Equal(HandshakeReask.RetryDelays.Count, run.Sends);
        // Backoff: each wait at least as long as the one before.
        for (int i = 1; i < HandshakeReask.RetryDelays.Count; i++) Assert.True(HandshakeReask.RetryDelays[i] >= HandshakeReask.RetryDelays[i - 1]);
        string line = HandshakeReask.KeptConnectedLine("Alice", 4);
        Assert.Contains("ADMIN NOTICE", line, StringComparison.Ordinal);
        Assert.Contains("stay connected", line, StringComparison.Ordinal);
        // A player who left meanwhile ends the run.
        Assert.Equal(HandshakeReaskStep.Done, Run(answersAtSend: 0, stillConnected: false).Last);
    }

    [Fact]
    public void Only_a_re_ask_of_a_remembered_user_is_soft_a_join_is_always_asked_in_full()
    {
        Assert.Equal(HandshakeAskKind.Join, HandshakeReask.KindFor(reask: false, rememberedAnswered: true));
        Assert.Equal(HandshakeAskKind.Join, HandshakeReask.KindFor(reask: true, rememberedAnswered: false));
        var slot = new Slot();
        var memory = slot.Generation();
        memory.Remember(3, Steam);
        // Another player on the same user index is not the one who answered.
        Assert.False(memory.Contains(3, Steam + 1));
        memory.Forget(3);
        Assert.False(slot.Generation().Contains(3, Steam));
        memory.Remember(1, 11UL);
        memory.Remember(2, 22UL);
        Assert.Equal("1:11;2:22", slot.Value);
        Assert.Equal(2, HandshakeSessionMemory.Decode("1:11;bad;2:22;-1:5;3:0").Count);
        Assert.Empty(HandshakeSessionMemory.Decode(null));
    }

    [Fact]
    public void The_handshake_wires_the_soft_re_ask_and_its_path_has_no_kick()
    {
        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs");
        string runtime = Source("src/Progenitor.Core/Runtime.cs");
        string refresh = Source("src/Progenitor.Core/Networking/HandshakeRefresh.cs");
        Assert.Contains("var reask = Networking.Handshake.BeginReask();", runtime);
        Assert.Contains("reask.Dispose();", runtime);
        Assert.Contains("if (HandshakeReask.KindFor(_reaskScope, remembered) == HandshakeAskKind.SoftReask)", handshake);
        // A join forgets; an accepted answer remembers; a failing answer and a disconnect forget.
        Assert.Contains("if (!_reaskScope)\n        {\n            try { SessionMemory.Forget(ev.UserIndex); }", handshake.Replace("\r\n", "\n"));
        Assert.Contains("try { SessionMemory.Remember(user.Index, user.PlatformId); }", handshake);
        Assert.Contains("!wasAlreadyOk && !rememberedOk && GateOnClientPolicy(", handshake);
        Assert.Contains("HandshakeReask.RefreshAllowed(AnswerWatch.GetStatus(index) == HandshakeAnswerWatch.Status.Ok, SoftReaskPending(index))", refresh);
        // The soft re-ask arms no deadline and never disconnects.
        int start = handshake.IndexOf("private static void SoftReaskUser(", StringComparison.Ordinal);
        int end = handshake.IndexOf("private static unsafe void OnRequest(", start, StringComparison.Ordinal);
        string soft = handshake.Substring(start, end - start);
        Assert.DoesNotContain("Disconnect", soft, StringComparison.Ordinal);
        Assert.DoesNotContain("SendResultAndKick", soft, StringComparison.Ordinal);
        Assert.DoesNotContain("ArmAnswerDeadline", soft, StringComparison.Ordinal);
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CastlesTests.RepoRootForDumpSources(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
