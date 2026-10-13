using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using Progenitor.Dev;
using Xunit;

namespace Progenitor.Tests;

/// <summary>Game-free coverage of the Dev bridge's user history (who is driving the bridge).</summary>
public class BridgeUsersTests
{
    private static BridgeWho Who(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return BridgeWho.FromRequest(doc.RootElement);
    }

    private static BridgeWho Session(string ai, string name, string id) =>
        new() { Ai = ai, Name = name, Id = id, Tool = "mcp", Pid = 100 };

    private sealed class Clock
    {
        public DateTime Now = new(2026, 10, 10, 12, 0, 0, DateTimeKind.Utc);
        public DateTime Get() => Now;
    }

    [Fact]
    public void Request_without_who_is_unknown_and_unknown_fields_are_ignored()
    {
        var none = Who("{\"id\":1,\"token\":\"t\",\"op\":\"ping\"}");
        Assert.Equal("unknown", none.Ai);
        Assert.Equal("", none.Id);

        var bad = Who("{\"who\":\"not an object\"}");
        Assert.Equal("unknown", bad.Ai);

        var typed = Who("{\"who\":{\"ai\":\"codex\",\"name\":7,\"id\":\"abc\",\"pid\":\"x\",\"extra\":{\"a\":1}}}");
        Assert.Equal("codex", typed.Ai);
        Assert.Equal("", typed.Name);
        Assert.Equal("abc", typed.Id);
        Assert.Null(typed.Pid);
    }

    [Fact]
    public void Who_fields_are_clipped_and_control_characters_removed()
    {
        var w = Who("{\"who\":{\"ai\":\"claude\",\"name\":\"line\\nbreak" + new string('x', 400) + "\",\"pid\":42}}");
        Assert.Equal(BridgeWho.MaxFieldLength, w.Name.Length);
        Assert.DoesNotContain('\n', w.Name);
        Assert.Equal(42, w.Pid);
    }

    [Fact]
    public void Key_prefers_session_id_then_ai_name_pid()
    {
        Assert.Equal("id:s1", Session("claude", "A", "s1").Key);
        Assert.Equal("who:grok|B|mcp|9", new BridgeWho { Ai = "grok", Name = "B", Tool = "mcp", Pid = 9 }.Key);
        Assert.Equal("who:unknown|||", new BridgeWho().Key);
    }

    [Fact]
    public void One_shot_tool_calls_without_a_session_are_one_user()
    {
        // call.mjs is a new process per request; its pid must not make every call a new user.
        var a = new BridgeWho { Tool = "call.mjs", Pid = 100 };
        var b = new BridgeWho { Tool = "call.mjs", Pid = 101 };
        Assert.Equal(a.Key, b.Key);
        var t = new BridgeUserTracker(null);
        t.Record(Session("claude", "A", "s1"), "ping");
        for (int i = 0; i < 5; i++) t.Record(new BridgeWho { Tool = "call.mjs", Pid = 200 + i }, "ping");
        var snap = t.Snapshot();
        Assert.Equal(5L, snap["current"]!["requests"]!.GetValue<long>());
        Assert.Single(snap["recent"]!.AsArray());
    }

    [Fact]
    public void Tracks_current_and_three_distinct_previous_users()
    {
        var clock = new Clock();
        var t = new BridgeUserTracker(null, clock.Get);
        foreach (var (ai, id) in new[] { ("claude", "a"), ("codex", "b"), ("grok", "c"), ("claude", "d"), ("codex", "e") })
        {
            t.Record(Session(ai, "Example session " + id, id), "ping");
            clock.Now = clock.Now.AddMinutes(1);
        }
        var snap = t.Snapshot();
        Assert.Equal("e", snap["current"]!["id"]!.GetValue<string>());
        var recent = snap["recent"]!.AsArray();
        Assert.Equal(new[] { "d", "c", "b" }, new[] { Id(recent[0]), Id(recent[1]), Id(recent[2]) });
    }

    [Fact]
    public void Returning_user_keeps_its_counts_and_is_not_duplicated()
    {
        var clock = new Clock();
        var t = new BridgeUserTracker(null, clock.Get);
        var a = Session("claude", "A", "a");
        var b = Session("codex", "B", "b");
        t.Record(a, "ping");
        t.Record(a, "logs");
        t.Record(b, "connect");
        t.Record(a, "status");

        var snap = t.Snapshot();
        Assert.Equal("a", Id(snap["current"]));
        Assert.Equal(3, snap["current"]!["requests"]!.GetValue<long>());
        Assert.Equal("status", snap["current"]!["lastOp"]!.GetValue<string>());
        var recent = snap["recent"]!.AsArray();
        Assert.Single(recent);
        Assert.Equal("b", Id(recent[0]));
        Assert.Equal("connect", recent[0]!["lastOp"]!.GetValue<string>());
    }

    [Fact]
    public void Previous_user_is_reported_only_within_the_share_window()
    {
        var clock = new Clock();
        var t = new BridgeUserTracker(null, clock.Get);
        Assert.Null(t.Record(Session("codex", "B", "b"), "connect"));
        clock.Now = clock.Now.AddMinutes(3);
        var prev = t.Record(Session("claude", "A", "a"), "ping");
        Assert.NotNull(prev);
        Assert.Equal("b", prev!.Id);
        Assert.Equal(180, prev.ToJson(clock.Now)["ageSeconds"]!.GetValue<long>());

        clock.Now = clock.Now.AddMinutes(11);
        Assert.Null(t.Record(Session("claude", "A", "a"), "ping"));
    }

    [Fact]
    public void Change_logs_one_line_per_user_per_window()
    {
        var clock = new Clock();
        var lines = new List<string>();
        var t = new BridgeUserTracker(null, clock.Get, lines.Add);
        t.Record(Session("codex", "Example B", "b"), "connect");
        clock.Now = clock.Now.AddMinutes(3);
        t.Record(Session("claude", "Example A", "a"), "ping");
        t.Record(Session("claude", "Example A", "a"), "logs");
        t.Record(Session("codex", "Example B", "b"), "ping");
        t.Record(Session("claude", "Example A", "a"), "ping");

        Assert.Equal(2, lines.Count);
        Assert.Equal("bridge: now used by codex \"Example B\" (b)", lines[0]);
        Assert.Equal("bridge: now used by claude \"Example A\" (a); previously codex \"Example B\" 3 min ago", lines[1]);
    }

    [Fact]
    public void History_survives_a_reload_and_a_corrupt_file_starts_empty()
    {
        var dir = Path.Combine(Path.GetTempPath(), "progenitor-users-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var path = Path.Combine(dir, BridgeUserTracker.FileName);
            var clock = new Clock();
            var t = new BridgeUserTracker(path, clock.Get);
            t.Record(Session("codex", "B", "b"), "connect");
            t.Record(Session("claude", "A", "a"), "ping");
            clock.Now = clock.Now.AddSeconds(1);
            t.Record(Session("claude", "A", "a"), "logs"); // throttled: not yet on disk
            t.Flush();

            var reloaded = new BridgeUserTracker(path, clock.Get);
            reloaded.Load();
            var snap = reloaded.Snapshot();
            Assert.Equal("a", Id(snap["current"]));
            Assert.Equal(2, snap["current"]!["requests"]!.GetValue<long>());
            Assert.Equal("b", Id(snap["recent"]!.AsArray()[0]));

            File.WriteAllText(path, "{ not json");
            var lines = new List<string>();
            var corrupt = new BridgeUserTracker(path, clock.Get, lines.Add);
            corrupt.Load();
            Assert.Null(corrupt.Snapshot()["current"]);
            Assert.Single(lines);
            corrupt.Record(Session("grok", "C", "c"), "ping");
            Assert.Contains("\"c\"", File.ReadAllText(path));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { }
        }
    }

    [Fact]
    public void Path_sits_beside_the_token()
    {
        var token = Path.Combine("cfg", "progenitor-dev.token");
        Assert.Equal(Path.Combine("cfg", "progenitor-dev.users.json"), BridgeUserTracker.PathBesideToken(token));
    }

    private static string Id(JsonNode? n) => n!["id"]!.GetValue<string>();
}
