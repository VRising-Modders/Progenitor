using System;
using System.Linq;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// PLAINDENY1: the refusal panel opens on plain sentences and a bullet per mod; the technical
/// lines move behind "Show details". The user, 2026-09-28: "It need to be easier to read, less
/// technical garbage."
/// </summary>
public class JoinRefusalPlainTests
{
    private static ModDenyRow Missing(string id, string version, string? package = null, string? url = null) =>
        new(id, version, null, ModDenyOrigin.ServerRequires, id + ": expected " + version + ", got missing", package, url);

    [Fact]
    public void The_screenshot_case_reads_plainly()
    {
        // Live 2026-09-28: server "V Rising" required necroarmy and sample; this client had neither.
        var report = new ModDenyReport(
            "Mod mismatch: progenitor.necroarmy: expected 0.1.0, got missing; progenitor.sample: expected 0.1.0, got missing",
            "Aqf27TEYT", "V Rising", "operator", "00b9749a0441bdc5", true,
            new[] { Missing("progenitor.necroarmy", "0.1.0"), Missing("progenitor.sample", "0.1.0") });

        Assert.Equal("Can't join V Rising", ModDenyText.PanelTitle(report.ServerName));
        var summary = ModDenyText.Summary(report);
        Assert.Equal(new[]
        {
            "This server needs 2 mods you don't have:",
            "• Necroarmy (0.1.0)",
            "• Sample (0.1.0)",
            "The server didn't say where to get them.",
            "Install them, restart the game, and join again.",
        }, summary);

        string plain = string.Join("\n", summary);
        Assert.DoesNotContain("example.", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("signed", plain, StringComparison.Ordinal);
        Assert.DoesNotContain("join check", plain, StringComparison.Ordinal);
        // The technical text is still there, behind Show details.
        Assert.Contains(ModDenyText.Details(report), l => l.StartsWith("The server's reason: ", StringComparison.Ordinal));
    }

    [Fact]
    public void Readable_names_come_from_the_lookup_then_the_package_then_the_id()
    {
        var before = ModDenyText.NameLookup;
        try
        {
            ModDenyText.NameLookup = id => id == "a.b.known" ? "Known Mod" : null;
            Assert.Equal("Known Mod", ModDenyText.DisplayName(Missing("a.b.known", "1.0")));
            Assert.Equal("HotDemo", ModDenyText.DisplayName(Missing("x.y", "1.0", package: "Progenitor-HotDemo")));
            Assert.Equal("Sample", ModDenyText.DisplayName(Missing("progenitor.sample", "1.0")));
            ModDenyText.NameLookup = _ => throw new InvalidOperationException();
            Assert.Equal("Sample", ModDenyText.DisplayName(Missing("progenitor.sample", "1.0")));
        }
        finally { ModDenyText.NameLookup = before; }
    }

    [Fact]
    public void A_source_is_named_on_the_bullet()
    {
        var report = new ModDenyReport("x", "h", "S", "", "", true,
            new[] { Missing("a.mod", "1.0", package: "Team-Alpha") });
        var summary = ModDenyText.Summary(report);
        Assert.Contains("• Alpha (1.0) — on Thunderstore as Team-Alpha", summary);
        Assert.DoesNotContain("The server didn't say where to get them.", summary);
    }

    [Fact]
    public void A_version_mismatch_says_both_versions()
    {
        var row = new ModDenyRow("a.mod", "2.0", "1.0", ModDenyOrigin.ServerRequires, "a.mod: expected 2.0, got 1.0");
        var summary = ModDenyText.Summary(new ModDenyReport("x", "h", "S", "", "", false, new[] { row }));
        Assert.Equal("This server needs a different version of 1 mod:", summary[0]);
        Assert.Equal("• Mod — it needs 2.0, you have 1.0", summary[1]);
    }

    [Fact]
    public void Your_own_mods_read_as_turn_them_off()
    {
        var rows = new[]
        {
            new ModDenyRow("progenitor.necroarmy", "0.1.0", "0.1.0", ModDenyOrigin.YouRequire, null),
            new ModDenyRow("progenitor.sample", "0.1.0", "0.1.0", ModDenyOrigin.YouRequire, null),
        };
        var summary = ModDenyText.Summary(new ModDenyReport("x", "h", "S", "", "", false, rows));
        Assert.Equal("2 of your mods only work on servers that run them too, and this one doesn't:", summary[0]);
        Assert.Equal("Turn them off to join this server.", summary.Last());
    }

    [Fact]
    public void No_rows_still_says_something_plain()
    {
        var summary = ModDenyText.Summary(new ModDenyReport("x", "h", "S", "", "", false, Array.Empty<ModDenyRow>()));
        Assert.Single(summary);
    }
}
