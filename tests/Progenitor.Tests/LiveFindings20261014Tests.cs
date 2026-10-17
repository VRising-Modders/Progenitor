using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using Progenitor.Dev;
using Progenitor.Dev.Ops;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Ui;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Three defects a mod author's live session found on 2026-10-14 (Core 2026.10.14.5): a server plugin hot-loaded from
/// <c>progenitor-reload</c> was never announced to connected clients (RESEND1), and the footer and a mod subtitle read
/// <c>Â·</c> in a bridge readback. The bridge's click on grayed controls is pinned in <c>CellRowTests</c> and
/// <c>ModSettingsStage2Tests</c>.
/// </summary>
[Collection("PluginLogger")]
public sealed class LiveFindings20261014Tests : IDisposable
{
    public LiveFindings20261014Tests() => Handshake.ResetTables();

    public void Dispose() => Handshake.ResetTables();

    // ---- RESEND1: the server's request is re-sent when its required table changes ------------------

    private static Handshake.ModRecord Row(string guid, string version = "1.0.0",
        NetworkCompatibilityLevel level = NetworkCompatibilityLevel.NoNeedForSync) =>
        Handshake.ModRecord.CreateDeclared(guid, version, level, NetworkVersionRule.Minor);

    /// <summary>
    /// The compare is per user (review M1): a join inside the settle window records only that user's stamp, so the users
    /// asked before the change are still stale and get the re-send. Same rows in another order are no change; a user never
    /// sent a request is not stale; a disconnect forgets. What this would catch: one shared "last sent" that a join resets,
    /// hiding the change from everyone else.
    /// </summary>
    [Fact]
    public void The_requirement_watch_compares_each_user_against_what_that_user_was_sent()
    {
        var watch = new HandshakeRequirementWatch();
        string ab = HandshakeRequirementWatch.Stamp(new[] { Row("a.mod"), Row("b.mod") });
        Assert.Equal(ab, HandshakeRequirementWatch.Stamp(new[] { Row("b.mod"), Row("a.mod") }));
        string abc = HandshakeRequirementWatch.Stamp(new[] { Row("a.mod"), Row("b.mod"), Row("c.mod") });

        watch.NoteSent(0, ab);
        watch.NoteSent(1, ab);
        // c.mod loads; user 2 joins inside the settle window and is asked with the new rows.
        watch.NoteSent(2, abc);
        Assert.True(watch.IsStale(0, abc));
        Assert.True(watch.IsStale(1, abc));
        Assert.False(watch.IsStale(2, abc), "the joiner already has the new rows");
        Assert.False(watch.IsStale(3, abc), "never sent a request: nothing to correct");
        // A swap that settles back to the same rows is no change.
        Assert.False(watch.IsStale(0, ab));
        // Another version or level is a change.
        Assert.True(watch.IsStale(0, HandshakeRequirementWatch.Stamp(new[] { Row("a.mod", "1.1.0"), Row("b.mod") })));
        Assert.True(watch.IsStale(0, HandshakeRequirementWatch.Stamp(new[]
            { Row("a.mod", "1.0.0", NetworkCompatibilityLevel.ServerMustHave), Row("b.mod") })));
        watch.Forget(0);
        Assert.False(watch.IsStale(0, abc));
        Assert.Equal("", HandshakeRequirementWatch.Stamp(Array.Empty<Handshake.ModRecord>()));
    }

    /// <summary>
    /// Owed answers are counted (review L1): two back-to-back re-sends owe two answers, so the first answer settling one
    /// leaves the second still treated as a re-send answer (a mismatch keeps the player connected). One schedule at a time,
    /// cleared by <c>Unschedule</c>. What this would catch: a yes/no flag the first answer clears, letting the second kick.
    /// </summary>
    [Fact]
    public void Owed_resend_answers_are_counted_and_the_schedule_is_single()
    {
        var watch = new HandshakeRequirementWatch();
        Assert.False(watch.TakeResendAnswer(4), "no re-send, no owed answer: judged as usual");
        watch.NoteResendSent(4);
        watch.NoteResendSent(4);
        Assert.Equal(2, watch.ResendsOwed(4));
        Assert.True(watch.TakeResendAnswer(4));
        Assert.True(watch.TakeResendAnswer(4), "the second answer is still a re-send answer");
        Assert.False(watch.TakeResendAnswer(4));
        watch.NoteResendSent(4);
        watch.Forget(4);
        Assert.Equal(0, watch.ResendsOwed(4));

        Assert.True(watch.TrySchedule());
        Assert.False(watch.TrySchedule(), "one compare at a time");
        watch.Unschedule();
        Assert.True(watch.TrySchedule());
        watch.Reset();
        Assert.False(watch.Scheduled);
    }

    /// <summary>
    /// A server plugin's <c>Require</c> on load and its <c>Unrequire</c> on unload schedule the compare, and the stamp of the
    /// request as packed now follows the table. What this would catch: the hook dropped from the table's writers, which is
    /// the defect (the request went out only at a join or a Core swap).
    /// </summary>
    [Fact]
    public void Require_and_Unrequire_schedule_the_compare_against_the_request_as_packed_now()
    {
        int scheduled = 0;
        Handshake.TestRequirementCompareScheduled = () => scheduled++;
        string before = Handshake.RequestStampNow();
        Handshake.RequirementWatch.NoteSent(0, before);

        Handshake.Require("example.server.part", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        Assert.Equal(1, scheduled);
        Assert.True(Handshake.RequirementWatch.Scheduled);
        var packed = Handshake.PackRequestNow(out string stamp);
        Assert.Contains(packed.Rows, r => r.Guid == "example.server.part");
        Assert.Equal(Handshake.RequestStampNow(), stamp);
        Assert.True(Handshake.RequirementWatch.IsStale(0, stamp), "user 0 was asked before the load");

        // Unload while the compare is still pending: no second schedule; the stamp is back where user 0 was.
        Handshake.Unrequire("example.server.part");
        Assert.Equal(1, scheduled);
        Assert.False(Handshake.RequirementWatch.IsStale(0, Handshake.RequestStampNow()), "a swap back is no change");
        Handshake.RequirementWatch.Unschedule();
        Handshake.Require("example.server.part", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        Assert.Equal(2, scheduled);
    }

    /// <summary>
    /// The live glue, source-checked (it needs the server world): every request is packed through <c>PackRequestNow</c> and
    /// records its stamp per user only after a successful send; the compare re-sends to ok users whose stamp differs and
    /// clears its schedule in a finally; an accepted answer from a stale user is re-sent (M2); an answer settles one owed
    /// re-send answer before either branch, and a mismatch then keeps an already-ok user connected.
    /// </summary>
    [Fact]
    public void The_live_resend_reaches_stale_ok_users_and_a_mismatch_in_its_answer_never_kicks()
    {
        // NOKICK1 (2026-10-17) widened this: every answer from an admitted player keeps them (NoKickOnServerChangeTests).
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(true, false, false, requirementResend: true));
        Assert.Equal(HandshakeMismatchOutcome.KeepInPlace, HandshakeReask.MismatchOutcome(true, false, false, requirementResend: false));
        Assert.Equal(HandshakeMismatchOutcome.Refuse, HandshakeReask.MismatchOutcome(false, false, false, requirementResend: false));
        string line = HandshakeReask.KeptAfterRequirementChangeLine("a player", new[] { "x" });
        Assert.Contains("their next join is checked against the new list", line, StringComparison.Ordinal);

        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs").Replace("\r\n", "\n");
        string refresh = Source("src/Progenitor.Core/Networking/HandshakeRefresh.cs").Replace("\r\n", "\n");
        Assert.DoesNotContain("Compare.PackRequest(RequestRowsNow())", handshake, StringComparison.Ordinal);
        Assert.DoesNotContain("Compare.PackRequest(RequestRowsNow())", refresh, StringComparison.Ordinal);
        Assert.Contains("if (sent) RequirementWatch.NoteSent(index, requestStamp);", refresh);
        Assert.Equal(2, Count(handshake, "if (sent) RequirementWatch.NoteSent(ev.UserIndex, requestStamp);"));
        Assert.Contains("Dev.MainThread.PostDelayed(HandshakeRequirementWatch.SettleSeconds, ResendRequestIfRequirementsChanged, DeadlineOwner);", handshake);

        string resend = Between(handshake, "private static void ResendRequestIfRequirementsChanged()", "private static unsafe void OnRequest(");
        Assert.Contains("if (AnswerWatch.GetStatus(user.Index) != HandshakeAnswerWatch.Status.Ok) continue;", resend);
        Assert.Contains("if (!RequirementWatch.IsStale(user.Index, now)) continue;", resend);
        Assert.Contains("finally\n        {\n            // Review L3", resend);
        Assert.Contains("if (!CustomEvents.SendToUser(world, entity, RequestEventId, list)) return false;\n        RequirementWatch.NoteSent(user.Index, stamp);\n        RequirementWatch.NoteResendSent(user.Index);", resend);
        Assert.Contains("PolicyAsksForReport(requestRows, user.Index, LabelOf(ev));", resend);
        Assert.DoesNotContain("Disconnect", resend, StringComparison.Ordinal);
        Assert.DoesNotContain("ArmAnswerDeadline", resend, StringComparison.Ordinal);

        int take = handshake.IndexOf("bool requirementResend = RequirementWatch.TakeResendAnswer(user.Index);", StringComparison.Ordinal);
        int okBranch = handshake.IndexOf("if (problems.Count == 0)", take, StringComparison.Ordinal);
        int keep = handshake.IndexOf("HandshakeReask.MismatchOutcome(wasAlreadyOk, rememberedOk, softReask, requirementResend)", StringComparison.Ordinal);
        int note = handshake.IndexOf("RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);", StringComparison.Ordinal);
        Assert.True(take > 0 && take < okBranch, "the owed answer is taken once, before either branch");
        Assert.True(keep > 0 && keep < note, "the keep-connected check comes before the refusal path");
        Assert.Contains("if (world != null && RequirementWatch.IsStale(user.Index, RequestStampNow()))", handshake);
        Assert.Contains("RequirementWatch.Forget(ev.User.Index);", handshake);
    }

    private static int Count(string text, string part)
    {
        int n = 0;
        for (int at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal)) n++;
        return n;
    }

    private static string Between(string text, string start, string end)
    {
        int a = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(a >= 0, start);
        int b = text.IndexOf(end, a, StringComparison.Ordinal);
        Assert.True(b > a, end);
        return text.Substring(a, b - a);
    }

    /// <summary>
    /// The page that started this: needs-server-part rows on a plugin that never names its server part in the handshake
    /// request stay gray on every server; registering such a page says so once. What this would catch: the silent gray.
    /// </summary>
    [Fact]
    public void A_needs_server_part_page_on_an_undeclared_plugin_is_flagged()
    {
        var builder = new SettingsPageBuilder("example.flag");
        builder.Button("Open", () => { }, needsServerPart: true);
        Assert.True(ModSettingsPages.ServerPartUndeclared(new object(), builder.Sections));
        Assert.False(ModSettingsPages.ServerPartUndeclared(new DeclaredPlugin(), builder.Sections));
        var plain = new SettingsPageBuilder("example.plain");
        plain.Button("Open", () => { });
        Assert.False(ModSettingsPages.ServerPartUndeclared(new object(), plain.Sections));
    }

    [NetworkCompatibility(NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor)]
    private sealed class DeclaredPlugin
    {
    }

    // ---- Mojibake: the footer and the subtitle carry U+00B7, and the readback escapes it ------------

    /// <summary>
    /// Live 2026-10-14: <c>2.0.0 Â· progenitor-reload</c> and <c>Progenitor Core 2026.10.14.5 Â· Bootstrap …</c> in a
    /// <c>mods_ui</c> status read through <c>tools/mcp/call.mjs</c> from Windows PowerShell; the screen drew both
    /// correctly. The strings are built with exactly one U+00B7 (no U+00C2), and both bridge serializers escape it to
    /// <c>\u00B7</c> ASCII, so the wire cannot carry the double encoding; <c>call.mjs</c> keeps that on its own stdout
    /// (<c>tools/mcp/test/call-mail.test.mjs</c>). What this would catch: a source file re-saved in another encoding, or
    /// a serializer that passes non-ASCII through.
    /// </summary>
    [Fact]
    public void The_footer_and_the_subtitle_carry_one_middle_dot_and_reach_the_bridge_as_an_ascii_escape()
    {
        string footer = FrameworkVersions.WorkspaceFooter("2026.10.14.5", "2026.10.14");
        Assert.Equal("Progenitor Core 2026.10.14.5 · Bootstrap 2026.10.14", footer);
        string subtitle = new ModsSidebarRow { Id = "example.reload", Version = "2.0.0", Provenance = ModProvenance.Reload }.Subtitle;
        Assert.Equal("2.0.0 · " + ModMenuInventory.WordOf(ModProvenance.Reload), subtitle);
        foreach (string s in new[] { footer, subtitle })
        {
            Assert.Equal(1, Count(s, '·'));
            Assert.Equal(0, Count(s, 'Â'));
        }

        var status = new Dictionary<string, object?> { ["footer"] = footer, ["entries"] = new[] { new Dictionary<string, object?> { ["subtitle"] = subtitle } } };
        foreach (var options in new[] { ModsUiResult.JsonOptions, BridgeJson.Options })
        {
            string json = JsonSerializer.Serialize(status, options);
            foreach (char c in json) Assert.True(c < 0x80, "non-ASCII on the wire: U+" + ((int)c).ToString("X4"));
            Assert.Contains("\\u00B7", json, StringComparison.Ordinal);
            Assert.DoesNotContain("\\u00C2", json, StringComparison.Ordinal);
            using var parsed = JsonDocument.Parse(json);
            Assert.Equal(footer, parsed.RootElement.GetProperty("footer").GetString());
        }
    }

    /// <summary>
    /// No source under <c>src</c> or <c>samples</c> carries UTF-8 read as Latin-1 (U+00C2 before U+0080..U+00BF: <c>Â·</c>,
    /// <c>Â»</c>, <c>Â§</c>). The notes that quote the symptom on purpose put it in <c>&lt;c&gt;</c> markup.
    /// </summary>
    [Fact]
    public void No_source_file_carries_double_encoded_text()
    {
        string root = CastlesTests.RepoRootForDumpSources();
        var hits = new List<string>();
        foreach (string dir in new[] { "src", "samples" })
            foreach (string file in Directory.EnumerateFiles(Path.Combine(root, dir), "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                    || file.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)) continue;
                string[] lines = File.ReadAllLines(file);
                for (int n = 0; n < lines.Length; n++)
                {
                    string line = lines[n];
                    for (int i = 0; i + 1 < line.Length; i++)
                    {
                        if (line[i] != 'Â' || line[i + 1] < '\u0080' || line[i + 1] > '¿') continue;
                        if (i > 0 && line.LastIndexOf("<c>", i, StringComparison.Ordinal) > line.LastIndexOf("</c>", i, StringComparison.Ordinal)) continue;
                        hits.Add(Path.GetRelativePath(root, file) + ":" + (n + 1));
                        break;
                    }
                }
            }

        Assert.Empty(hits);
    }

    private static int Count(string s, char c)
    {
        int n = 0;
        foreach (char x in s) if (x == c) n++;
        return n;
    }

    private static string Source(string relative) =>
        File.ReadAllText(Path.Combine(CastlesTests.RepoRootForDumpSources(), relative.Replace('/', Path.DirectorySeparatorChar)));
}
