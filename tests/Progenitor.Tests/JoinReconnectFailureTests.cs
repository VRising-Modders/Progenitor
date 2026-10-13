using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Live 2026-10-05: the game's loading screen showed "Error: Connection timed out" while the Joining page waited for its
/// own 120 s. The reconnect now ends at once on the game's own connect failure, with its reason.
/// </summary>
public sealed class JoinReconnectFailureTests
{
    private sealed class Host : IJoinConnectHost
    {
        public bool World;
        public bool Menu = true;
        public bool Failed;
        public bool Kicked;
        public string Reason = "Timeout";
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => false;
        public void RequestLeave() => Calls.Add("leave");
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch");
            return true;
        }

        public bool TryConnectFailure(out string reasonName, out bool kicked)
        {
            reasonName = Failed ? Reason : "";
            kicked = Kicked;
            return Failed;
        }
    }

    private static JoinTarget Target() => new() { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" };

    [Fact]
    public void The_games_connect_failure_ends_the_reconnect_at_once_with_its_reason()
    {
        var host = new Host();
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(1);
        Assert.Equal("connecting", r.Phase);
        host.World = true;
        r.Tick(2);
        Assert.Equal("connecting", r.Phase);
        host.Failed = true;
        r.Tick(5);
        Assert.Equal("failed", r.Phase);
        Assert.Equal("Connection timed out", r.Error);
    }

    [Fact]
    public void A_failure_already_showing_when_the_connect_starts_is_not_this_connects()
    {
        var host = new Host { Failed = true };
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(1);
        r.Tick(2);
        Assert.Equal("connecting", r.Phase);
        host.Failed = false;
        r.Tick(3);
        host.Failed = true;
        r.Tick(4);
        Assert.Equal("failed", r.Phase);
    }

    [Fact]
    public void A_kick_waits_a_moment_for_the_refusals_own_screens()
    {
        var host = new Host();
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(1);
        r.Tick(2);
        host.Failed = true;
        host.Kicked = true;
        host.Reason = "Kicked";
        r.Tick(3);
        Assert.Equal("connecting", r.Phase);
        r.Tick(3 + JoinReconnect.KickGraceSeconds);
        Assert.Equal("failed", r.Phase);
        Assert.Equal("The server disconnected you", r.Error);
    }

    [Fact]
    public void Join_now_still_fails_on_a_password_refusal()
    {
        // Only the Server tab's Join (PromptOnPasswordRefusal) turns a password refusal into the game's prompt.
        var host = new Host { Reason = JoinReconnect.IncorrectPasswordReason };
        var r = new JoinReconnect(Target(), host, 0);
        r.Tick(1);
        r.Tick(2);
        host.Failed = true;
        r.Tick(3);
        Assert.Equal("failed", r.Phase);
        Assert.Equal("Wrong password", r.Error);
        Assert.False(r.PasswordRefused);
    }

    [Theory]
    [InlineData("Timeout", "Connection timed out")]
    [InlineData("ServerFull", "The server is full")]
    [InlineData("IncorrectPassword", "Wrong password")]
    [InlineData("Unknown", "The connection failed")]
    [InlineData("SomethingNew", "Something new")]
    public void The_games_reasons_read_in_words(string name, string words)
    {
        Assert.Equal(words, JoinText.ConnectFailureWords(name));
    }
}
