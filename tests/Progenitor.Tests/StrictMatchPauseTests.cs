using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Progenitor.Networking;
using Progenitor.Plugins;
using Xunit;
using FakeBackend = Progenitor.Tests.ServerModPauseTests.FakeBackend;
using Harness = Progenitor.Tests.ServerModPauseTests.Harness;
using ModRecord = Progenitor.Networking.Handshake.ModRecord;

namespace Progenitor.Tests;

/// <summary>
/// Mod compatibility C3 (decision 4): under Exact version for all or Same minor for all, a both-sides optional mod
/// (NoNeedForSync) at another version is paused for this server when it can be paused; otherwise nothing is paused and
/// the server refuses the join by itself. The client learns the mode from the request bits. Game-free.
/// </summary>
[Collection("Handshake")]
public sealed class StrictMatchPauseTests : IDisposable
{
    private readonly string _dir;

    public StrictMatchPauseTests()
    {
        ServerModPause.ResetForTests();
        ServerProfiles.ResetForTests();
        ModCompatCaps.Reset();
        _dir = Path.Combine(Path.GetTempPath(), "progenitor-strict-pause-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        ServerProfiles.TestDirectory = _dir;
    }

    public void Dispose()
    {
        ServerModPause.ResetForTests();
        ModCompatCaps.Reset();
        ServerProfiles.TestDirectory = null;
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    /// <summary>The client's own row: a both-sides optional mod.</summary>
    private static ModRecord Optional(string guid, string version) =>
        ModRecord.CreateDeclared(guid, version, NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);

    /// <summary>The server's request row as the server rewrites it under a strict mode.</summary>
    private static ModRecord Asked(string guid, string version, NetworkVersionRule rule = NetworkVersionRule.Exact,
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.NoNeedForSync) =>
        ModRecord.CreateDeclared(guid, version, level, rule);

    private static Func<IReadOnlyList<string>, (IReadOnlyList<string>, string)> ClosureOf(FakeBackend backend) =>
        g => { var set = backend.Closure(g, out var r); return (set, r); };

    private static ServerModPausePlan Plan(FakeBackend b, IReadOnlyList<ModRecord> asked, bool pauseOptional) =>
        ServerModPausePolicy.PlanPause(asked, false, b.Provided(), b.Owners(), b.Suspendable().ToList(), ClosureOf(b),
            autoPause: true, pauseOptional: pauseOptional);

    [Fact]
    public void An_optional_mod_at_another_version_is_paused_only_under_strict_matching()
    {
        var b = new FakeBackend();
        b.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        var asked = new[] { Asked("chat", "1.2.0") };

        var ranges = Plan(b, asked, pauseOptional: false);
        Assert.Empty(ranges.Pause);

        var strict = Plan(b, asked, pauseOptional: true);
        Assert.Equal(new[] { "chat" }, strict.Pause);
        Assert.Equal(new[] { "chat" }, strict.StrictMatch);
        Assert.Equal("", strict.Why);

        // Same minor for all: a version of the same minor passes and nothing is paused.
        var sameMinor = Plan(b, new[] { Asked("chat", "1.3.5", NetworkVersionRule.Minor) }, pauseOptional: true);
        Assert.Empty(sameMinor.Pause);
        Assert.Equal(new[] { "chat" }, Plan(b, new[] { Asked("chat", "1.2.0", NetworkVersionRule.Minor) }, true).Pause);
    }

    [Fact]
    public void An_optional_mod_that_cannot_be_paused_leaves_the_refusal_to_the_server()
    {
        var b = new FakeBackend();
        b.Add("chat", "chat.dll", Optional("chat", "1.3.0")).Reloadable = false;
        var plan = Plan(b, new[] { Asked("chat", "1.2.0") }, pauseOptional: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(new[] { "chat" }, plan.RestartNeeded);
        Assert.Equal(ServerModPausePolicy.WhyRestartOnly, plan.Why);
    }

    [Fact]
    public void Other_plugins_the_closure_would_take_are_still_protected()
    {
        var b = new FakeBackend();
        b.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        b.Add("emotes", "emotes.dll", Optional("emotes", "1.0.0")).Deps = new[] { "chat" };
        var plan = Plan(b, new[] { Asked("chat", "1.2.0") }, pauseOptional: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyProtected, plan.Why);

        // A library that declares nothing may be taken with it.
        var lib = new FakeBackend();
        lib.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        lib.Add("chat.lib", "chat.dll", ModRecord.Undeclared("chat.lib", "1.0.0"));
        var withLib = Plan(lib, new[] { Asked("chat", "1.2.0") }, pauseOptional: true);
        Assert.Equal(new[] { "chat", "chat.lib" }, withLib.Pause.OrderBy(x => x).ToArray());
        Assert.Equal(new[] { "chat" }, withLib.StrictMatch);
    }

    [Fact]
    public void A_mod_the_server_requires_is_not_paused_away()
    {
        // The server declares it ServerMustHave: a client without it is refused, so pausing would not help.
        var b = new FakeBackend();
        b.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        var plan = Plan(b, new[] { Asked("chat", "1.2.0", level: NetworkCompatibilityLevel.ServerMustHave) }, pauseOptional: true);
        Assert.Empty(plan.Pause);
        Assert.Equal(ServerModPausePolicy.WhyNotClean, plan.Why);
    }

    [Fact]
    public void A_paused_optional_mod_the_strict_server_refuses_stays_paused_on_the_next_request()
    {
        var paused = new[] { new PausedPlugin("chat", "Chat", "1.3.0", rows: new[] { "chat" }) };
        var asked = new[] { Asked("chat", "1.2.0") };
        Assert.Equal(new[] { "chat" }, ServerModPausePolicy.KeepOptionalPaused(asked, paused, pauseOptional: true));
        Assert.Empty(ServerModPausePolicy.KeepOptionalPaused(asked, paused, pauseOptional: false));
        Assert.Empty(ServerModPausePolicy.KeepOptionalPaused(new[] { Asked("chat", "1.3.0") }, paused, true));
        var hard = new[] { new PausedPlugin("chat", "Chat", "1.3.0", rows: new[] { "chat" }, hardRows: new[] { "chat" }) };
        Assert.Empty(ServerModPausePolicy.KeepOptionalPaused(asked, hard, true));
    }

    [Fact]
    public void The_notice_reason_names_the_strict_mode()
    {
        Assert.Equal("Another version than this server's (exact match required)", ServerModPauseText.ReasonStrictMatch(VersionMatchingMode.Exact));
        Assert.Equal("Another version than this server's (same minor required)", ServerModPauseText.ReasonStrictMatch(VersionMatchingMode.Minor));
        Assert.Equal("", ServerModPauseText.ReasonStrictMatch(VersionMatchingMode.Range));
    }

    [Fact]
    public void The_reply_path_pauses_under_the_requests_Exact_bit_and_says_why()
    {
        var h = new Harness();
        h.Backend.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        h.Backend.Add("maps", "maps.dll", ModRecord.CreateDeclared("maps", "1.0.0", NetworkCompatibilityLevel.ServerMustHave, NetworkVersionRule.Minor));
        ModCompatCaps.NoteServerCaps(Handshake.Capability.MatchExact);

        h.Request(Asked("chat", "1.2.0"));
        h.Pump();

        Assert.True(h.Paused("chat"));
        Assert.True(h.Paused("maps"));   // a ServerMustHave mod the server lacks: today's pause, with its own reason
        var reply = Assert.Single(h.Replies);
        Assert.DoesNotContain(reply, r => r.Guid == "chat");
        Assert.Contains(h.Notices, n => n.Contains("CHAT (Another version than this server's (exact match required))")
            && n.Contains("MAPS (" + ServerModPauseText.ReasonServerLacks + ")"));

        // The next request from the same strict server does not resume it only to pause it again.
        h.Backend.Calls.Clear();
        h.Request(Asked("chat", "1.2.0"));
        h.Pump();
        Assert.True(h.Paused("chat"));
        Assert.DoesNotContain(h.Backend.Calls, c => c.StartsWith("resume:chat", StringComparison.Ordinal));
    }

    [Fact]
    public void Without_a_strict_bit_the_reply_path_keeps_todays_refusal()
    {
        var h = new Harness();
        h.Backend.Add("chat", "chat.dll", Optional("chat", "1.3.0"));
        h.Request(Asked("chat", "1.2.0"));
        h.Pump();
        Assert.False(h.Paused("chat"));
        Assert.Contains(Assert.Single(h.Replies), r => r.Guid == "chat");
    }
}
