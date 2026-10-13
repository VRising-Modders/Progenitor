using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Networking;
using Progenitor.Ui;
using Xunit;
using UiApi = Progenitor.Ui.Ui;

namespace Progenitor.Tests;

/// <summary>
/// SERVEREMPTY1: the Server tab's not-connected state — the last-server record (what is remembered, that no password is
/// ever written or read, which joins count), the Join's hidden / busy / ready rules, and the Join itself through Join now's
/// reconnect or the game's password prompt. The Mods-window rows are in <c>ThreeHomesMenuTests</c>. Game-free.
/// </summary>
[Collection("ModManifest")]
public sealed class ServerTabEmptyStateTests : IDisposable
{
    private readonly string _dir;
    private readonly FakeHost _host = new();
    private double _now = 100;
    private bool _passwordUsed;

    public ServerTabEmptyStateTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-server-empty-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        Plugin.Logger = new TestLogSource();
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = _dir;
        UiApi.ResetForTests();
        JoinFlowClient.Clock = () => _now;
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "SteamIPv4" };
        JoinFlowClient.PasswordProbe = () => _passwordUsed;
        JoinFlowClient.ConnectHost = _host;
    }

    public void Dispose()
    {
        JoinFlowClient.Uninstall();
        JoinFlowClient.Clock = () => System.Diagnostics.Stopwatch.GetTimestamp() / (double)System.Diagnostics.Stopwatch.Frequency;
        ModManifest.ResetForTests();
        ModManifest.TestConfigDirectory = null;
        UiApi.ResetForTests();
        Plugin.Logger = null;
        try { Directory.Delete(_dir, recursive: true); }
        catch { /* temp */ }
    }

    private sealed class FakeHost : IJoinConnectHost
    {
        public bool World;
        public bool Menu = true;
        public bool Joined;
        public bool PromptAvailable = true;
        public readonly List<string> Calls = new();

        public bool WorldExists => World;
        public bool MainMenuReady => Menu && !World;
        public bool JoinedOk => Joined && World;
        public void RequestLeave() => Calls.Add("leave");
        public void RequestMainMenu() => Calls.Add("menu");

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            Calls.Add("launch:" + target.Protocol + "://" + target.Endpoint);
            return true;
        }

        public bool LaunchPasswordPrompt(JoinTarget target, out string error)
        {
            error = PromptAvailable ? "" : "no prompt";
            if (PromptAvailable) Calls.Add("prompt:" + target.Protocol + "://" + target.Endpoint);
            return PromptAvailable;
        }

        /// <summary>The game's connect failure by reason name (<c>IncorrectPassword</c>), or empty for none.</summary>
        public string FailureReason = "";

        public bool TryConnectFailure(out string reasonName, out bool kicked)
        {
            reasonName = FailureReason;
            kicked = false;
            return FailureReason.Length > 0;
        }
    }

    private static LastServerRecord Record(bool password = false, string name = "Progenitor Forms Test") => new()
    {
        Address = "127.0.0.1",
        Port = 9976,
        Protocol = "SteamIPv4",
        ServerName = name,
        NeedsPassword = password,
        Joined = "2026-10-06T12:00:00Z",
    };

    private string RecordPath => Path.Combine(_dir, LastServerStore.FileName);

    // ---- the record ------------------------------------------------------------------------------

    [Fact]
    public void The_record_round_trips_and_never_holds_a_password()
    {
        var record = Record(password: true);
        string json = record.ToJson();
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("\"needsPassword\": true", json);
        Assert.True(LastServerRecord.TryParse(json, out var back));
        Assert.Equal("Progenitor Forms Test", back!.ServerName);
        Assert.Equal("127.0.0.1:9976", back.Endpoint);
        Assert.True(back.NeedsPassword);
        // A password someone put in the file is not read, and is not written back.
        string tampered = json.Replace("\"address\"", "\"password\": \"hunter2\", \"address\"");
        Assert.True(LastServerRecord.TryParse(tampered, out var read));
        Assert.DoesNotContain("hunter2", read!.ToJson());
        Assert.Null(typeof(LastServerRecord).GetProperty("Password"));
        // JOINPW1: needsPassword is a hint, never the decision: Join always connects without a password first and opens
        // the game's prompt only on a password refusal.
        foreach (var target in new[] { read.ToTarget(), Record().ToTarget() })
        {
            Assert.False(target.PromptForPassword);
            Assert.False(target.NeedsPassword);
            Assert.True(target.PromptOnPasswordRefusal);
        }
    }

    [Theory]
    [InlineData("{\"schema\":2,\"address\":\"127.0.0.1\",\"port\":9976,\"protocol\":\"SteamIPv4\"}")]
    [InlineData("{\"schema\":1,\"address\":\"\",\"port\":9976,\"protocol\":\"SteamIPv4\"}")]
    [InlineData("{\"schema\":1,\"address\":\"127.0.0.1\",\"port\":0,\"protocol\":\"SteamIPv4\"}")]
    [InlineData("{\"schema\":1,\"address\":\"127.0.0.1\",\"port\":9976,\"protocol\":\"SteamP2P\"}")]
    [InlineData("{\"schema\":1,\"address\":\"127.0.0.1\",\"port\":9976}")]
    [InlineData("not json")]
    [InlineData("")]
    public void A_record_that_cannot_be_dialed_reads_as_none(string json)
    {
        Assert.False(LastServerRecord.TryParse(json, out var r));
        Assert.Null(r);
        File.WriteAllText(RecordPath, json);
        Assert.Null(LastServerStore.Read(RecordPath));
    }

    [Fact]
    public void Only_a_dialable_join_to_a_server_this_client_does_not_host_is_remembered()
    {
        var now = new DateTime(2026, 10, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.NotNull(LastServerRecord.From(new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "Lidgren" }, now));
        Assert.Null(LastServerRecord.From(new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", HostedByThisClient = true }, now));
        Assert.Null(LastServerRecord.From(new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "EosP2P" }, now));
        Assert.Null(LastServerRecord.From(new JoinTarget { Address = "127.0.0.1", Port = 9976, Protocol = "" }, now));
        Assert.Null(LastServerRecord.From(new JoinTarget { Address = "", Port = 9976, Protocol = "SteamIPv4" }, now));
        Assert.Null(LastServerRecord.From(null, now));
    }

    [Fact]
    public void The_store_writes_atomically_and_reads_back()
    {
        LastServerStore.Write(RecordPath, Record());
        Assert.False(File.Exists(RecordPath + ".tmp"));
        var back = LastServerStore.Read(RecordPath);
        Assert.NotNull(back);
        Assert.True(back!.SameAs(Record()));
        Assert.False(back.SameAs(Record(password: true)));
        Assert.Null(LastServerStore.Read(Path.Combine(_dir, "missing.json")));
    }

    // ---- recorded on a successful join -----------------------------------------------------------------

    [Fact]
    public void A_successful_join_writes_the_last_server_without_its_password()
    {
        _passwordUsed = true;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));

        // In the world, not yet answered: nothing.
        _host.World = true;
        _now += 2;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));

        _host.Joined = true;
        _now += 2;
        JoinFlowClient.Tick();
        var record = LastServerStore.Read(RecordPath);
        Assert.NotNull(record);
        Assert.Equal("127.0.0.1:9976", record!.Endpoint);
        Assert.Equal("SteamIPv4", record.Protocol);
        Assert.True(record.NeedsPassword);
        string json = File.ReadAllText(RecordPath);
        Assert.DoesNotContain("\"password\"", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(record, JoinFlowClient.ReadLastServer(), new SameRecord());
    }

    [Fact]
    public void A_server_without_Progenitor_counts_once_it_was_detected()
    {
        _host.World = true;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));
        JoinFlowClient.NoteJoinedWithoutProgenitor();
        _now += 2;
        JoinFlowClient.Tick();
        Assert.NotNull(LastServerStore.Read(RecordPath));

        // Leaving clears it: the next world is not counted until it is joined.
        _host.World = false;
        _now += 2;
        JoinFlowClient.Tick();
        File.Delete(RecordPath);
        _host.World = true;
        _now += 2;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));
    }

    [Fact]
    public void A_join_the_game_names_as_another_server_is_not_recorded_as_the_dialed_one()
    {
        // Bootstrap's dialed address is never cleared: a join that did not pass through LaunchGame keeps the old one.
        JoinFlowClient.LiveHostProbe = () => "10.0.0.5:9876";
        _host.World = true;
        _host.Joined = true;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));

        // The game naming the same address (any case), or no address at all (a relay string), records it.
        _host.World = false;
        _now += 2;
        JoinFlowClient.Tick();
        JoinFlowClient.LiveHostProbe = () => "127.0.0.1:9976";
        _host.World = true;
        _now += 2;
        JoinFlowClient.Tick();
        Assert.NotNull(LastServerStore.Read(RecordPath));
        Assert.True(JoinFlowClient.LastServerMatchesGame(Record(), out _));
        JoinFlowClient.LiveHostProbe = () => "Aqf27THwTQgI4Hk6FpSBAvJNEAA";
        Assert.True(JoinFlowClient.LastServerMatchesGame(Record(), out _));
    }

    [Fact]
    public void A_private_game_this_client_hosts_is_never_recorded()
    {
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", HostedByThisClient = true };
        _host.World = true;
        _host.Joined = true;
        JoinFlowClient.Tick();
        Assert.False(File.Exists(RecordPath));
    }

    // ---- frame cost in a joined world (2026-10-06 stutter) --------------------------------------------

    [Fact]
    public void In_a_joined_world_the_record_is_looked_at_once_while_its_inputs_are_unchanged()
    {
        // Live: the name lookup behind each look scans the Steam libraries and reads settings files; it cost the
        // frame 34-211 ms every 10 s with no join screen open.
        int lookups = 0;
        JoinFlowClient.ConnectedNameLookup = () =>
        {
            lookups++;
            return "Folder Name";
        };
        _host.World = true;
        _host.Joined = true;
        JoinFlowClient.Tick();
        Assert.NotNull(LastServerStore.Read(RecordPath));
        Assert.Equal(1, JoinFlowClient.LastServerFullLooks);
        int afterFirst = lookups;
        Assert.True(afterFirst > 0);

        // Ten minutes of frames in the same world: no further full look, no further name lookup.
        for (int i = 0; i < 6000; i++)
        {
            _now += 0.1;
            JoinFlowClient.Tick();
        }

        Assert.Equal(1, JoinFlowClient.LastServerFullLooks);
        Assert.Equal(afterFirst, lookups);

        // A changed input (another port, a password now used) looks again and rewrites the record.
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9977, Protocol = "SteamIPv4" };
        _now += 11;
        JoinFlowClient.Tick();
        Assert.Equal(2, JoinFlowClient.LastServerFullLooks);
        Assert.Equal(9977, LastServerStore.Read(RecordPath)!.Port);
        _passwordUsed = true;
        _now += 11;
        JoinFlowClient.Tick();
        Assert.Equal(3, JoinFlowClient.LastServerFullLooks);
        Assert.True(LastServerStore.Read(RecordPath)!.NeedsPassword);
    }

    [Fact]
    public void A_world_that_is_never_recorded_is_not_looked_at_every_second()
    {
        // Live 2026-10-06, a private game this client hosted: no record is ever written, so the old tick repeated the
        // full look once a second (about 32 ms each, 214-224 ms every 30 s when the server picker rescanned).
        int lookups = 0;
        JoinFlowClient.ConnectedNameLookup = () =>
        {
            lookups++;
            return "";
        };
        JoinFlowClient.TargetProbe = () => new JoinTarget { Address = "127.0.0.1", Port = 9876, Protocol = "SteamIPv4", HostedByThisClient = true };
        _host.World = true;
        _host.Joined = true;
        for (int i = 0; i < 600; i++)
        {
            _now += 0.1;
            JoinFlowClient.Tick();
        }

        Assert.False(File.Exists(RecordPath));
        Assert.Equal(1, JoinFlowClient.LastServerFullLooks);
        Assert.True(lookups <= 1);
    }

    [Fact]
    public void The_idle_join_tick_allocates_nothing_between_looks()
    {
        _host.World = true;
        _host.Joined = true;
        for (int i = 0; i < 50; i++)
        {
            _now += 0.25;
            JoinFlowClient.Tick();
        }

        // 100 frames at 100 fps (one second, inside the 10 s recheck): the steady-state frame is field reads and locks.
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
        {
            _now += 0.0099;
            JoinFlowClient.Tick();
        }

        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
    }

    // ---- the Join ---------------------------------------------------------------------------------------

    [Fact]
    public void The_join_is_hidden_without_a_record_or_while_hosting_and_busy_while_a_join_runs()
    {
        Assert.Equal(LastServerJoinState.Hidden, new LastServerFacts().JoinState);
        Assert.Equal(LastServerJoinState.Hidden, new LastServerFacts { Record = Record(), HostingPrivateGame = true }.JoinState);
        Assert.Equal(LastServerJoinState.Busy, new LastServerFacts { Record = Record(), JoinInProgress = true }.JoinState);
        Assert.Equal(LastServerJoinState.Ready, new LastServerFacts { Record = Record() }.JoinState);
        // Only the not-connected state draws the row.
        var ready = new LastServerFacts { Record = Record() };
        Assert.Equal(LastServerJoinState.Ready, ServerTabPane.LastServerStateOf(ServerTabState.NotConnected, ready));
        foreach (var state in new[] { ServerTabState.Waiting, ServerTabState.NoProgenitor, ServerTabState.Ready })
            Assert.Equal(LastServerJoinState.Hidden, ServerTabPane.LastServerStateOf(state, ready));
        Assert.Equal("Progenitor Forms Test · 127.0.0.1:9976", ServerTabPane.LastServerText(Record()));
        Assert.Equal("127.0.0.1:9976", ServerTabPane.LastServerText(Record(name: "")));
    }

    [Fact]
    public void Join_connects_through_join_nows_reconnect()
    {
        Assert.Equal("joining", JoinFlowClient.JoinLastServer(Record()));
        var session = JoinFlowClient.Session;
        Assert.NotNull(session);
        Assert.Equal(JoinMode.Rejoin, session!.Mode);
        Assert.Equal("Progenitor Forms Test", session.RejoinView!.ServerName);
        Assert.True(JoinFlowClient.JoinInProgress);
        // A second press while it runs does nothing.
        Assert.Equal("a join is already in progress", JoinFlowClient.JoinLastServer(Record()));
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);
        // The world on its way in is a join in progress too.
        _host.World = true;
        Assert.True(JoinFlowClient.JoinInProgress);
    }

    // ---- JOINPW1: no password first; the game's prompt only after a password refusal ------------------------

    [Fact]
    public void A_stale_password_hint_connects_without_a_password_and_a_join_clears_it()
    {
        // Live 2026-10-06: the record kept needsPassword from an earlier join; the server no longer has a password.
        LastServerStore.Write(RecordPath, Record(password: true));
        Assert.Equal("joining", JoinFlowClient.JoinLastServer(JoinFlowClient.ReadLastServer()));
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);

        // Joined without a password: the hint becomes false.
        _host.World = true;
        _host.Joined = true;
        _now += 2;
        JoinFlowClient.Tick();
        _now += 2;
        JoinFlowClient.Tick();
        var record = LastServerStore.Read(RecordPath);
        Assert.NotNull(record);
        Assert.False(record!.NeedsPassword);
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);
        Assert.DoesNotContain("\"password\"", File.ReadAllText(RecordPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_password_refusal_opens_the_games_prompt_and_notes_the_hint()
    {
        LastServerStore.Write(RecordPath, Record(password: false));
        Assert.Equal("joining", JoinFlowClient.JoinLastServer(JoinFlowClient.ReadLastServer()));
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976" }, _host.Calls);

        // The connect is under way, then the server refuses it for its password.
        _host.World = true;
        _now += 1;
        JoinFlowClient.Tick();
        _host.FailureReason = JoinReconnect.IncorrectPasswordReason;
        _now += 1;
        JoinFlowClient.Tick();
        Assert.True(LastServerStore.Read(RecordPath)!.NeedsPassword);
        Assert.Equal(JoinText.PasswordRefusedNote, JoinFlowClient.Session!.RejoinView!.Status);
        Assert.True(JoinFlowClient.JoinInProgress);

        // Back to the title view (the game's Leave), then the game's own prompt for the same address.
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Contains("leave", _host.Calls);
        _host.World = false;
        _host.FailureReason = "";
        _now += 1;
        JoinFlowClient.Tick();
        Assert.Equal("prompt:SteamIPv4://127.0.0.1:9976", _host.Calls[^1]);
        Assert.Single(_host.Calls, c => c.StartsWith("prompt:", StringComparison.Ordinal));
        // The Joining page steps aside for the prompt; nothing waits on the connect.
        Assert.True(JoinFlowClient.Session == null || JoinFlowClient.Session.Closed);
        Assert.False(JoinFlowClient.JoinInProgress);
        Assert.False(File.Exists(Path.Combine(_dir, RejoinStore.FileName)));
        Assert.DoesNotContain("\"password\"", File.ReadAllText(RecordPath), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void A_cancelled_prompt_just_stops()
    {
        var host = new FakeHost();
        var r = new JoinReconnect(Record(password: true).ToTarget(), host, 0);
        r.Tick(1);
        r.Tick(2);
        host.FailureReason = JoinReconnect.IncorrectPasswordReason;
        r.Tick(3);
        Assert.True(r.PasswordRefused);
        Assert.Equal("leaving", r.Phase);
        Assert.True(r.TakePasswordRefusal());
        Assert.False(r.TakePasswordRefusal());
        host.FailureReason = "";
        r.Tick(4);
        Assert.Equal("prompted", r.Phase);
        Assert.True(r.Finished);
        // The player cancels the game's prompt: the title view is back, and nothing connects again.
        for (int i = 5; i < 200; i += 10) r.Tick(i);
        Assert.Equal("prompted", r.Phase);
        Assert.Equal(new[] { "launch:SteamIPv4://127.0.0.1:9976", "prompt:SteamIPv4://127.0.0.1:9976" }, host.Calls);
    }

    [Fact]
    public void Another_failure_after_no_password_still_fails_with_its_reason()
    {
        var host = new FakeHost();
        var r = new JoinReconnect(Record().ToTarget(), host, 0);
        r.Tick(1);
        r.Tick(2);
        host.FailureReason = "Timeout";
        r.Tick(3);
        Assert.Equal("failed", r.Phase);
        Assert.Equal("Connection timed out", r.Error);
        Assert.False(r.PasswordRefused);
        Assert.DoesNotContain(host.Calls, c => c.StartsWith("prompt:", StringComparison.Ordinal));
    }

    [Fact]
    public void A_refusal_never_marks_another_servers_record()
    {
        LastServerStore.Write(RecordPath, Record(password: false));
        JoinFlowClient.NoteLastServerPasswordRefused(new JoinTarget { Address = "10.0.0.5", Port = 9876, Protocol = "SteamIPv4" });
        Assert.False(LastServerStore.Read(RecordPath)!.NeedsPassword);
        JoinFlowClient.NoteLastServerPasswordRefused(Record().ToTarget());
        Assert.True(LastServerStore.Read(RecordPath)!.NeedsPassword);
    }

    [Fact]
    public void Without_a_password_prompt_a_refused_join_fails_with_the_reason()
    {
        var host = new PromptlessHost();
        var r = new JoinReconnect(Record(password: true).ToTarget(), host, 0);
        r.Tick(1);
        Assert.Equal("connecting", r.Phase);
        r.Tick(2);
        host.Refused = true;
        r.Tick(3);
        host.Refused = false;
        r.Tick(4);
        Assert.Equal("failed", r.Phase);
        Assert.Equal("the game's password prompt is not available here", r.Error);
    }

    /// <summary>Keeps the interface's default for the password prompt, and refuses for the password.</summary>
    private sealed class PromptlessHost : IJoinConnectHost
    {
        public bool Refused;
        public bool WorldExists => false;
        public bool MainMenuReady => true;
        public bool JoinedOk => false;
        public void RequestLeave() { }
        public void RequestMainMenu() { }

        public bool Launch(JoinTarget target, out string error)
        {
            error = "";
            return true;
        }

        public bool TryConnectFailure(out string reasonName, out bool kicked)
        {
            reasonName = Refused ? JoinReconnect.IncorrectPasswordReason : "";
            kicked = false;
            return Refused;
        }
    }

    [Fact]
    public void Join_is_refused_while_hosting_or_without_a_reconnect_host()
    {
        JoinFlowClient.TargetProbe = () => new JoinTarget { HostedByThisClient = true };
        Assert.Equal("a private game this client hosts is running", JoinFlowClient.JoinLastServer(Record()));
        Assert.Equal("no last server", JoinFlowClient.JoinLastServer(null));
        JoinFlowClient.ConnectHost = null;
        Assert.Equal("reconnecting is not available here", JoinFlowClient.JoinLastServer(Record()));
        Assert.Empty(_host.Calls);
    }

    private sealed class SameRecord : IEqualityComparer<LastServerRecord?>
    {
        public bool Equals(LastServerRecord? x, LastServerRecord? y) => x != null && x.SameAs(y);
        public int GetHashCode(LastServerRecord? obj) => 0;
    }
}
