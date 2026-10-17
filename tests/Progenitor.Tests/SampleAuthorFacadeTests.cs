using System.Collections.Generic;
using Progenitor.Cameras;
using Progenitor.Players.Forms;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free parse and reply coverage for the author samples of <c>ctx.Items</c>,
/// <c>ctx.Debug</c>, <c>ctx.Players</c>, <c>ctx.Camera</c> and experimental <c>PlayerForms</c>.
/// The live calls sit behind <c>PROGENITOR_TESTS</c> in the sample files.
/// </summary>
public class SampleAuthorFacadeTests
{
    [Theory]
    [InlineData("grant", "Item_BloodRose", "", SampleItems.Kind.Grant, 1, 0)]
    [InlineData("grant", "-123", "4", SampleItems.Kind.Grant, 4, 0)]
    [InlineData(" equip ", "3", "", SampleItems.Kind.Equip, 1, 3)]
    internal void Items_known_shapes_parse(string verb, string token, string extra, SampleItems.Kind kind, int amount, int slot)
    {
        Assert.True(SampleItems.TryParse(verb, token, extra, out var parsed, out _, out int gotAmount, out int gotSlot));
        Assert.Equal(kind, parsed);
        Assert.Equal(amount, gotAmount);
        Assert.Equal(slot, gotSlot);
    }

    [Theory]
    [InlineData("grant", "", "")]
    [InlineData("grant", "Rose", "0")]
    [InlineData("grant", "Rose", "10001")]
    [InlineData("equip", "-1", "")]
    [InlineData("equip", "35", "")]
    [InlineData("drop", "1", "")]
    public void Items_refuses_a_bad_request(string verb, string token, string extra)
    {
        Assert.False(SampleItems.TryParse(verb, token, extra, out _, out _, out _, out _));
    }

    [Fact]
    public void Items_replies_name_the_result_fields()
    {
        Assert.Contains("added=2/5", SampleItems.GrantReply(true, "added", 5, 2, 4, false, "short stack"));
        Assert.Contains("slot=4", SampleItems.GrantReply(true, "added", 5, 2, 4, false, null));
        Assert.StartsWith("Grant refused.", SampleItems.GrantReply(false, "refused", 1, 0, -1, false, "inventory is full"));
        Assert.Contains("bag slot 3", SampleItems.EquipReply(true, "equipped", 3, true, null));
        Assert.StartsWith("Equip refused", SampleItems.EquipReply(false, "refused", 3, false, "empty"));
    }

    [Theory]
    [InlineData("on", SampleDebugProtect.Kind.On)]
    [InlineData("ALIVE", SampleDebugProtect.Kind.Alive)]
    [InlineData(" off ", SampleDebugProtect.Kind.Off)]
    internal void Protect_tokens_parse(string token, SampleDebugProtect.Kind kind)
    {
        Assert.True(SampleDebugProtect.TryParse(token, out var parsed));
        Assert.Equal(kind, parsed);
    }

    [Fact]
    public void Protect_reply_names_the_facade_method_and_the_result_flags()
    {
        string on = SampleDebugProtect.Reply(SampleDebugProtect.Kind.On, true, true, true, true, true);
        Assert.StartsWith("Protect applied.", on);
        Assert.Contains("ignoredByFoes=True", on);
        Assert.StartsWith("KeepAlive applied.", SampleDebugProtect.Reply(SampleDebugProtect.Kind.Alive, true, true, true, false, true));
        Assert.StartsWith("Release did not fully apply.", SampleDebugProtect.Reply(SampleDebugProtect.Kind.Off, false, false, false, false, false));
        Assert.False(SampleDebugProtect.TryParse("status", out _));
    }

    [Fact]
    public void Players_parse_and_do_not_collapse_an_ambiguous_name()
    {
        Assert.True(SamplePlayerDirectory.TryParse("", "", out var online, out _, out _));
        Assert.Equal(SamplePlayerDirectory.Kind.Online, online);
        Assert.True(SamplePlayerDirectory.TryParse("name", "Ada", out var byName, out string name, out _));
        Assert.Equal(SamplePlayerDirectory.Kind.Name, byName);
        Assert.Equal("Ada", name);
        Assert.True(SamplePlayerDirectory.TryParse("account", "42", out var byAccount, out _, out ulong id));
        Assert.Equal(SamplePlayerDirectory.Kind.Account, byAccount);
        Assert.Equal(42ul, id);
        Assert.False(SamplePlayerDirectory.TryParse("account", "0", out _, out _, out _));
        Assert.False(SamplePlayerDirectory.TryParse("name", "", out _, out _, out _));

        var rows = new List<SamplePlayerDirectory.Row>
        {
            new("Ada", true, 7, true, "user=1:1 char=2:1 slot=0"),
            new("Ada", true, 8, true, "user=3:1 char=4:1 slot=1"),
        };
        string ambiguous = SamplePlayerDirectory.NameReply("Ada", "Ambiguous", null, rows);
        Assert.Contains("ambiguous", ambiguous);
        Assert.Contains("Not picking one.", ambiguous);
        Assert.Contains("platform=7", ambiguous);
        Assert.Contains("platform=8", ambiguous);

        string exact = SamplePlayerDirectory.NameReply("Ada", "Exact", rows[0], rows);
        Assert.Contains("matched one player", exact);
        Assert.DoesNotContain("platform=8", exact);

        string listed = SamplePlayerDirectory.OnlineReply(3, rows);
        Assert.StartsWith("2 online (directory revision 3).", listed);
        Assert.Contains("fresh=True", listed);
    }

    [Fact]
    public void Forms_list_the_experimental_catalog_and_parse_a_begin()
    {
        Assert.True(SampleForms.TryParse("", "", out var list, out _, out _));
        Assert.Equal(SampleForms.Kind.List, list);
        Assert.True(SampleForms.TryParse("begin", "Lord Styx 15", out var begin, out string name, out double seconds));
        Assert.Equal(SampleForms.Kind.Begin, begin);
        Assert.Equal("Lord Styx", name);
        Assert.Equal(15, seconds);
        Assert.True(SampleForms.TryParse("begin", "Lord Styx", out _, out string named, out double namedSeconds));
        Assert.Equal("Lord Styx", named);
        Assert.Equal(SampleForms.DefaultSeconds, namedSeconds);
        Assert.True(SampleForms.TryParse("begin", "", out _, out string bare, out double fallback));
        Assert.Equal("", bare);
        Assert.Equal(SampleForms.DefaultSeconds, fallback);
        Assert.True(SampleForms.TryParse("return", "", out var back, out _, out _));
        Assert.Equal(SampleForms.Kind.Return, back);
        Assert.False(SampleForms.TryParse("begin", "0", out _, out _, out _));
        Assert.False(SampleForms.TryParse("begin", "301", out _, out _, out _));

        string catalog = SampleForms.CatalogReply(false);
        Assert.Contains("IsReady=False", catalog);
        Assert.Contains("Lord Styx", catalog);
        Assert.Contains("expected to be refused", catalog);
        Assert.Contains(PlayerForms.AvailableForms[0].PrefabGuid.ToString(), catalog);

        Assert.True(SampleForms.TryPick("", out int guid, out string label, out _));
        Assert.Equal(PlayerForms.AvailableForms[0].PrefabGuid, guid);
        Assert.Equal("Lord Styx", label);
        Assert.False(SampleForms.TryPick("not-a-form", out _, out _, out string? error));
        Assert.Contains("No listed form", error);

        string refused = SampleForms.BeginReply(false, "no profile is qualified", 4, "pending");
        Assert.Contains("refused", refused);
        Assert.Contains("id=4", refused);
        Assert.Contains("tracked", refused);
        Assert.Contains("No handle", SampleForms.BeginReply(false, "unavailable", null, null));
    }

    [Fact]
    public void Shot_parse_covers_mode_orbit_point_and_both_free_shots()
    {
        Assert.True(SampleCamera.TryParse("", "", "", "", "", "", "", out var status));
        Assert.Equal((int)SampleCamera.Kind.Status, status.Kind);

        Assert.True(SampleCamera.TryParse("mode", "orbit", "", "", "", "", "", out var mode));
        Assert.Equal((int)CameraMode.Orbit, mode.Mode);

        Assert.True(SampleCamera.TryParse("orbit", "8", "10", "20", "", "", "", out var orbit));
        Assert.Equal((int)SampleCamera.Kind.OrbitSelf, orbit.Kind);
        Assert.Equal(8f, orbit.Distance);
        Assert.Equal(10f, orbit.Yaw);
        Assert.Equal(20f, orbit.Pitch);

        Assert.True(SampleCamera.TryParse("point", "1", "2", "3", "9", "4", "5", out var point));
        Assert.Equal((int)SampleCamera.Kind.OrbitPoint, point.Kind);
        Assert.Equal(1f, point.X);
        Assert.Equal(9f, point.Distance);

        Assert.True(SampleCamera.TryParse("free", "", "", "", "", "", "", out var free));
        Assert.Equal((int)SampleCamera.Kind.FreeLook, free.Kind);

        Assert.True(SampleCamera.TryParse("shot", "15", "-10", "focus", "", "", "", out var shot));
        Assert.Equal((int)SampleCamera.Kind.FreeShot, shot.Kind);
        Assert.Equal(1, shot.Focus);
        Assert.Equal(15f, shot.Yaw);

        Assert.True(SampleCamera.TryParse("reset", "", "", "", "", "", "", out var reset));
        Assert.Equal((int)SampleCamera.Kind.Reset, reset.Kind);

        Assert.False(SampleCamera.TryParse("mode", "none", "", "", "", "", "", out _));
        Assert.False(SampleCamera.TryParse("orbit", "0", "", "", "", "", "", out _));
        Assert.False(SampleCamera.TryParse("shot", "1", "", "", "", "", "", out _));
        Assert.Contains(".shot mode", SampleCamera.UsageReply);
        Assert.Contains("your client", SampleCamera.SentReply("orbit"));
    }

    [Fact]
    public void Shot_parse_covers_preview_hold_and_end()
    {
        Assert.True(SampleCamera.TryParse("preview", "", "", "", "", "", "", out var timed));
        Assert.Equal((int)SampleCamera.Kind.Preview, timed.Kind);
        Assert.Equal(0, timed.Hold);

        Assert.True(SampleCamera.TryParse("preview", "HOLD", "", "", "", "", "", out var held));
        Assert.Equal((int)SampleCamera.Kind.Preview, held.Kind);
        Assert.Equal(1, held.Hold);

        Assert.True(SampleCamera.TryParse("hold", "", "", "", "", "", "", out var hold));
        Assert.Equal((int)SampleCamera.Kind.Hold, hold.Kind);
        Assert.True(SampleCamera.TryParse("end", "", "", "", "", "", "", out var end));
        Assert.Equal((int)SampleCamera.Kind.End, end.Kind);

        Assert.False(SampleCamera.TryParse("preview", "forever", "", "", "", "", "", out _));
        Assert.False(SampleCamera.TryParse("hold", "10", "", "", "", "", "", out _));
        Assert.Contains(".shot preview [hold]", SampleCamera.UsageReply);
    }

    [Fact]
    public void A_stale_preview_ended_report_does_not_match_the_running_lease()
    {
        // Preview 1 is replaced by preview 2; the client's late "1 ended (Replaced)" must leave lease 2 running.
        Assert.False(SampleCamera.ReportMatches(runningSequence: 2, reportedSequence: 1));
        Assert.True(SampleCamera.ReportMatches(runningSequence: 2, reportedSequence: 2));
        // A preview the server never numbered (no character) matches nothing.
        Assert.False(SampleCamera.ReportMatches(runningSequence: 0, reportedSequence: 0));
    }
}
