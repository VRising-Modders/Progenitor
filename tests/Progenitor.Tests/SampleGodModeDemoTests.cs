using Progenitor.GodMode;
using Progenitor.Sample;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Job FSAMP. Game-free coverage of <c>.godmode</c>: token parse and the three replies. The
/// point being pinned is that a refused <c>Add</c> is never reported as success — <c>Add</c>
/// answers <c>false</c> for "already guarded", "client process" and "the prefixes are not
/// installed", and only <c>Describe()</c> tells those apart, so every reply carries it.
/// </summary>
[Collection("PluginLogger")]
public class SampleGodModeDemoTests
{
    [Theory]
    [InlineData("", SampleGodModeDemo.Kind.Status)]
    [InlineData("status", SampleGodModeDemo.Kind.Status)]
    [InlineData("on", SampleGodModeDemo.Kind.On)]
    [InlineData(" OFF ", SampleGodModeDemo.Kind.Off)]
    [InlineData("static", SampleGodModeDemo.Kind.Static)]
    [InlineData(" Sweep ", SampleGodModeDemo.Kind.Sweep)]
    internal void Known_tokens_parse(string token, SampleGodModeDemo.Kind expected)
    {
        Assert.True(SampleGodModeDemo.TryParse(token, out var kind));
        Assert.Equal(expected, kind);
    }

    [Theory]
    [InlineData("true")]
    [InlineData("enable")]
    public void An_unknown_token_is_refused_so_the_command_replies_usage(string token)
    {
        Assert.False(SampleGodModeDemo.TryParse(token, out _));
        Assert.Equal("Usage: .godmode on | off | static | sweep | status", SampleGodModeDemo.UsageReply);
    }

    [Fact]
    public void A_successful_add_says_guarded_and_carries_Describe()
    {
        string reply = SampleGodModeDemo.OnReply(added: true, alreadyGuarded: false, "godmode: guarded=1 patched=3");

        Assert.StartsWith("Guarded.", reply);
        Assert.Contains("godmode: guarded=1 patched=3", reply);
    }

    [Fact]
    public void An_already_guarded_entity_is_not_reported_as_a_refusal()
    {
        string reply = SampleGodModeDemo.OnReply(added: false, alreadyGuarded: true, "godmode: guarded=1 patched=3");

        Assert.StartsWith("Already guarded; nothing changed.", reply);
        Assert.DoesNotContain("NOT guarded", reply);
    }

    [Fact]
    public void A_refused_add_says_the_character_is_not_protected_and_explains_patched_zero()
    {
        string reply = SampleGodModeDemo.OnReply(added: false, alreadyGuarded: false, "godmode: guarded=0 patched=0");

        Assert.StartsWith("NOT guarded.", reply);
        Assert.Contains("not protected", reply);
        Assert.Contains("patched=0 means the prefix group did not install", reply);
        Assert.Contains("up to 3 times per Core generation", reply);
    }

    [Fact]
    public void Off_distinguishes_a_removed_guard_from_one_that_was_never_there()
    {
        Assert.StartsWith(
            "Guard removed.",
            SampleGodModeDemo.OffReply(removed: true, stillGuarded: false, "godmode: guarded=0 patched=3"));
        Assert.StartsWith(
            "There was no guard of this command's on your character.",
            SampleGodModeDemo.OffReply(removed: false, stillGuarded: false, "godmode: guarded=0 patched=3"));
    }

    /// <summary>
    /// R40 finding 5. <c>.godmode off</c> drops only this command's hold, so a character
    /// Core's <c>.debug</c> still owns stays unkillable — and the reply has to say so, or the admin
    /// reads "Guard removed." and tests a kill path that will not kill them.
    /// </summary>
    [Fact]
    public void Off_says_when_another_owner_still_guards_the_character()
    {
        string reply = SampleGodModeDemo.OffReply(
            removed: true, stillGuarded: true,
            "godmode: guarded=1 patched=3 owners: 45:1=[progenitor.debug]");

        Assert.StartsWith("Guard removed.", reply);
        Assert.Contains("STILL guarded by another owner", reply);
        Assert.Contains("only drops its own hold", reply);
        Assert.Contains("owners: 45:1=[progenitor.debug]", reply);
    }

    /// <summary>
    /// <c>.godmode</c> and Core's <c>.debug</c> must not share an owner token — asserted through the real guard
    /// set rather than by comparing two literals (R41 finding 7's test note). The property R40
    /// finding 5 needs is behavioral: with both holds taken on one character, dropping
    /// <c>.godmode</c>'s leaves <c>.debug</c>'s standing and the character still guarded.
    /// </summary>
    [Fact]
    public void The_two_sample_guard_owners_are_distinct()
    {
        // The debug buff's label (DebugBuff.GuardOwnerLabel, now in Core), spelled out so this
        // Sample test does not depend on Core's debug module.
        const string noDrainLabel = "progenitor.debug";
        var plugin = new object();
        ulong character = GodModeGuard.Key(45, 1);

        GodModeGuard.Clear();
        Progenitor.WorldState.WorldEpoch.ResetForTests();
        try
        {
            var noDrain = new GodModeGuard.GodModeOwner(plugin, noDrainLabel);
            var godMode = new GodModeGuard.GodModeOwner(plugin, SampleGodModeDemo.GuardOwner);

            Assert.True(GodModeGuard.Add(character, noDrain));
            // A distinct owner, so this is a second hold and not "already guarded".
            Assert.True(GodModeGuard.Add(character, godMode));

            // `.godmode off` drops its own hold only.
            Assert.True(GodModeGuard.Remove(character, godMode));
            Assert.False(GodModeGuard.IsGuardedBy(character, godMode));
            Assert.True(GodModeGuard.IsGuardedBy(character, noDrain));
            Assert.True(GodModeGuard.IsGuarded(character));
            Assert.Contains(noDrainLabel, GodModeGuard.DescribeOwners());

            // And `.debug`'s clear then unguards it.
            Assert.True(GodModeGuard.Remove(character, noDrain));
            Assert.False(GodModeGuard.IsGuarded(character));
        }
        finally
        {
            GodModeGuard.Clear();
            Progenitor.WorldState.WorldEpoch.ResetForTests();
        }
    }

    [Fact]
    public void Status_reports_the_guard_state_and_Describe()
    {
        Assert.StartsWith("Your character is guarded.", SampleGodModeDemo.StatusReply(true, "godmode: guarded=1 patched=3"));
        Assert.StartsWith("Your character is not guarded.", SampleGodModeDemo.StatusReply(false, "godmode: guarded=0 patched=0"));
        Assert.Contains("godmode: guarded=0 patched=0", SampleGodModeDemo.StatusReply(false, "godmode: guarded=0 patched=0"));
    }

    [Fact]
    public void A_user_with_no_character_is_told_so_rather_than_guarding_nothing()
    {
        Assert.Equal("No character entity on this user; there is nothing to guard.", SampleGodModeDemo.NoCharacterReply);
    }

    // ---- Job J21: the static route and the stale sweep ----

    /// <summary>
    /// The static route's reply has to say which of the two calls ran and whether anything
    /// changed, because the toggle is the only thing distinguishing "you now hold it" from "you
    /// already did".
    /// </summary>
    [Fact]
    public void The_static_reply_distinguishes_added_already_held_removed_and_held_nothing()
    {
        Assert.StartsWith(
            "Guarded through the static GodMode.Add(entity, ctx.Owner).",
            SampleGodModeDemo.StaticReply(nowGuarded: true, changed: true, "godmode: guarded=1 patched=3"));
        Assert.StartsWith(
            "The static route already held your character",
            SampleGodModeDemo.StaticReply(nowGuarded: true, changed: false, "godmode: guarded=1 patched=3"));
        Assert.StartsWith(
            "Dropped the static GodMode.Remove(entity, ctx.Owner) hold.",
            SampleGodModeDemo.StaticReply(nowGuarded: false, changed: true, "godmode: guarded=0 patched=3"));
        Assert.StartsWith(
            "The static route held nothing on your character.",
            SampleGodModeDemo.StaticReply(nowGuarded: false, changed: false, "godmode: guarded=0 patched=3"));
    }

    /// <summary>
    /// The whole reason to document the static form separately is the owner argument, and the
    /// failure it invites: a private token the unload sweep cannot reach leaves an unkillable
    /// character behind. The reply has to say that, or the example teaches the wrong shape.
    /// </summary>
    [Fact]
    public void The_static_reply_warns_that_a_private_owner_token_is_not_swept()
    {
        string reply = SampleGodModeDemo.StaticReply(true, true, "godmode: guarded=1 patched=3");

        Assert.Contains("takes the owner per call", reply, System.StringComparison.Ordinal);
        Assert.Contains("A private token would leave an unkillable character behind", reply, System.StringComparison.Ordinal);
        Assert.Contains("godmode: guarded=1 patched=3", reply, System.StringComparison.Ordinal);
    }

    /// <summary>
    /// Zero is the normal answer, so the reply must not read as a failure - an operator who runs
    /// this after a save reload and sees "dropped 0" should learn why that is expected.
    /// </summary>
    [Fact]
    public void The_sweep_reply_reports_the_count_and_the_epoch_and_says_zero_is_normal()
    {
        string reply = SampleGodModeDemo.SweepReply(dropped: 0, epoch: 4, "godmode: guarded=0 patched=3");

        Assert.Contains("GodModeGuard.SweepStale() dropped 0 guard(s)", reply, System.StringComparison.Ordinal);
        Assert.Contains("world-load epoch 4", reply, System.StringComparison.Ordinal);
        Assert.Contains("Zero is the normal answer", reply, System.StringComparison.Ordinal);
        Assert.Contains("godmode: guarded=0 patched=3", reply, System.StringComparison.Ordinal);
        Assert.Contains("2", SampleGodModeDemo.SweepReply(2, 1, ""), System.StringComparison.Ordinal);
    }

    /// <summary>
    /// The two J21 routes are behind <c>!PROGENITOR_TESTS</c> (both take an <c>Entity</c>), so the
    /// calls themselves are pinned in the source. The owner passed to the static pair is the pin
    /// that matters: <c>ctx.Owner</c> is what the unload sweep is handed.
    /// </summary>
    [Fact]
    public void The_static_route_passes_ctx_Owner_and_the_sweep_calls_SweepStale()
    {
        string src = Source();

        SourcePin.AssertStatement(src, "bool held = Progenitor.GodMode.GodMode.IsGuardedBy(character, plugin.Owner);");
        SourcePin.AssertStatement(src, "? Progenitor.GodMode.GodMode.Remove(character, plugin.Owner)");
        SourcePin.AssertStatement(src, ": Progenitor.GodMode.GodMode.Add(character, plugin.Owner);");
        SourcePin.AssertStatement(src, "int sweepDropped = Progenitor.GodMode.GodModeGuard.SweepStale();");
    }

    /// <summary>
    /// The sweep is about the guard set, not about the caller, so it must run before the command
    /// gives up on a user with no character. Pinned by position, because a later edit that folded
    /// it into the switch would silently make it unreachable for exactly the admin most likely to
    /// need it.
    /// </summary>
    [Fact]
    public void The_sweep_runs_before_the_character_check()
    {
        string src = Source();
        int sweep = src.IndexOf("if (kind == Kind.Sweep)", System.StringComparison.Ordinal);
        int character = src.IndexOf("var character = ctx.CharacterEntity;", System.StringComparison.Ordinal);

        Assert.True(sweep > 0, "the sweep branch is gone");
        Assert.True(character > 0, "the character read is gone");
        Assert.True(sweep < character, "the sweep must run before the character check");
    }

    private static string Source()
    {
        var dir = new System.IO.DirectoryInfo(System.AppContext.BaseDirectory);
        while (dir != null)
        {
            string candidate = System.IO.Path.Combine(
                dir.FullName, "samples", "Progenitor.Sample", "SampleGodModeDemo.cs");
            if (System.IO.File.Exists(candidate))
                return System.IO.File.ReadAllText(candidate).Replace("\r\n", "\n");
            dir = dir.Parent;
        }

        throw new System.IO.FileNotFoundException(
            "samples/Progenitor.Sample/SampleGodModeDemo.cs not found from " + System.AppContext.BaseDirectory);
    }
}
