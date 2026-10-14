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
    /// The compare: nothing sent yet is a baseline, not a change; the same rows in another order are no change; a row
    /// added, removed or with another version is. What this would catch: a re-send to every client on each swap, or none
    /// after a load.
    /// </summary>
    [Fact]
    public void The_requirement_watch_resends_only_when_the_rows_differ_from_the_last_request()
    {
        var watch = new HandshakeRequirementWatch();
        string a = HandshakeRequirementWatch.Stamp(new[] { Row("a.mod"), Row("b.mod") });
        string ba = HandshakeRequirementWatch.Stamp(new[] { Row("b.mod"), Row("a.mod") });
        Assert.Equal(a, ba);

        Assert.True(watch.TrySchedule());
        Assert.False(watch.TrySchedule(), "one compare at a time");
        Assert.False(watch.Due(a), "nothing was sent yet: a baseline");
        Assert.False(watch.Scheduled);

        // A join was asked with [a, b]; a swap (unload, load) settles to the same rows: nothing to send.
        watch.NoteSent(a);
        Assert.True(watch.TrySchedule());
        Assert.False(watch.Due(ba));

        // A server plugin loaded: a third row. Then it unloaded again.
        string abc = HandshakeRequirementWatch.Stamp(new[] { Row("a.mod"), Row("b.mod"), Row("c.mod") });
        Assert.True(watch.TrySchedule());
        Assert.True(watch.Due(abc));
        Assert.False(watch.Due(abc), "sent once");
        Assert.True(watch.Due(a));
        // Another version or level is a change too.
        Assert.True(watch.Due(HandshakeRequirementWatch.Stamp(new[] { Row("a.mod", "1.1.0"), Row("b.mod") })));
        Assert.True(watch.Due(HandshakeRequirementWatch.Stamp(new[]
            { Row("a.mod", "1.1.0", NetworkCompatibilityLevel.ServerMustHave), Row("b.mod") })));
        Assert.Equal("", HandshakeRequirementWatch.Stamp(Array.Empty<Handshake.ModRecord>()));
    }

    /// <summary>
    /// A server plugin's <c>Require</c> on load and its <c>Unrequire</c> on unload schedule the compare; the request
    /// helper every send goes through notes the rows it sent. What this would catch: the hook dropped from the table's
    /// writers, which is the defect (the request went out only at a join or a Core swap).
    /// </summary>
    [Fact]
    public void Require_and_Unrequire_schedule_the_compare_and_every_request_notes_its_rows()
    {
        Handshake.Require("example.server.part", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        Assert.True(Handshake.RequirementWatch.Scheduled);
        Assert.False(Handshake.RequirementWatch.Due(HandshakeRequirementWatch.Stamp(Handshake.RequestRowsNow())));

        // A join is asked with the current rows (PackRequestNow notes them).
        var packed = Handshake.PackRequestNow();
        Assert.Contains(packed.Rows, r => r.Guid == "example.server.part");
        Assert.False(Handshake.RequirementWatch.Due(HandshakeRequirementWatch.Stamp(Handshake.RequestRowsNow())));

        // The plugin unloads mid-session: the compare is scheduled and finds a change.
        Handshake.Unrequire("example.server.part");
        Assert.True(Handshake.RequirementWatch.Scheduled);
        Assert.True(Handshake.RequirementWatch.Due(HandshakeRequirementWatch.Stamp(Handshake.RequestRowsNow())));

        // Loads again: a change back.
        Handshake.Require("example.server.part", "2.0.0", NetworkCompatibilityLevel.NoNeedForSync, NetworkVersionRule.Minor);
        Assert.True(Handshake.RequirementWatch.Scheduled);
        Assert.True(Handshake.RequirementWatch.Due(HandshakeRequirementWatch.Stamp(Handshake.RequestRowsNow())));
    }

    /// <summary>
    /// The live glue, source-checked (it needs the server world): every request is packed through
    /// <c>PackRequestNow</c>; the settled compare re-sends to handshake-ok users only, with the policy's capability row,
    /// and marks them; a mismatch in that answer keeps an already-ok user connected; disconnect and uninstall forget.
    /// </summary>
    [Fact]
    public void The_live_resend_reaches_ok_users_and_a_mismatch_in_its_answer_never_kicks()
    {
        Assert.True(HandshakeReask.KeepConnectedOnMismatch(requirementResend: true, wasAlreadyOk: true));
        Assert.False(HandshakeReask.KeepConnectedOnMismatch(requirementResend: false, wasAlreadyOk: true));
        Assert.False(HandshakeReask.KeepConnectedOnMismatch(requirementResend: true, wasAlreadyOk: false));
        Assert.Contains("stay connected", HandshakeReask.KeptAfterRequirementChangeLine("a player", new[] { "x" }), StringComparison.Ordinal);

        string handshake = Source("src/Progenitor.Core/Networking/Handshake.cs").Replace("\r\n", "\n");
        string refresh = Source("src/Progenitor.Core/Networking/HandshakeRefresh.cs");
        Assert.DoesNotContain("Compare.PackRequest(RequestRowsNow())", handshake, StringComparison.Ordinal);
        Assert.DoesNotContain("Compare.PackRequest(RequestRowsNow())", refresh, StringComparison.Ordinal);
        Assert.Contains("Dev.MainThread.PostDelayed(HandshakeRequirementWatch.SettleSeconds, ResendRequestIfRequirementsChanged, DeadlineOwner);", handshake);

        int start = handshake.IndexOf("private static void ResendRequestIfRequirementsChanged()", StringComparison.Ordinal);
        Assert.True(start > 0);
        int end = handshake.IndexOf("private static unsafe void OnRequest(", start, StringComparison.Ordinal);
        string resend = handshake.Substring(start, end - start);
        Assert.Contains("if (AnswerWatch.GetStatus(user.Index) != HandshakeAnswerWatch.Status.Ok) continue;", resend);
        Assert.Contains("PolicyAsksForReport(requestRows, user.Index, LabelOf(ev));", resend);
        Assert.Contains("RequirementResends.Add(user.Index)", resend);
        Assert.DoesNotContain("Disconnect", resend, StringComparison.Ordinal);
        Assert.DoesNotContain("ArmAnswerDeadline", resend, StringComparison.Ordinal);

        int keep = handshake.IndexOf("HandshakeReask.KeepConnectedOnMismatch(requirementResend,", StringComparison.Ordinal);
        int note = handshake.IndexOf("RefusedJoins.Note(user.Index, failedIds, server, answered, user.PlatformId);", StringComparison.Ordinal);
        Assert.True(keep > 0 && keep < note, "the keep-connected check comes before the refusal path");
        Assert.Contains("lock (RequirementResends) RequirementResends.Remove(ev.User.Index);", handshake);
        Assert.Contains("RequirementWatch.Reset();", handshake);
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
    /// <c>·</c> ASCII, so the wire cannot carry the double encoding; <c>call.mjs</c> keeps that on its own stdout
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
