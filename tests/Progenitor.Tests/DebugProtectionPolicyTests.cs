using System.Collections.Generic;
using Progenitor.Buffs;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// 2026-10-07 facade smoke, problem 2: <c>ctx.Debug</c>'s <c>KeepAlive</c> destroyed Core's
/// <c>.debug</c> buff, <c>Release</c> destroyed all three buffs whoever owned them, and the
/// same-frame readback answered <c>ok=False</c> because <c>DestroyUtility.Destroy</c> lands next
/// frame. These drive <see cref="DebugProtectionPolicy"/> through a small model of one
/// character, the same bookkeeping <c>DebugProtection.Converge</c> does.
/// </summary>
public class DebugProtectionPolicyTests
{
    private const DebugProtectionBuffs Debug = DebugProtectionBuffs.Debug;
    private const DebugProtectionBuffs Invuln = DebugProtectionBuffs.Invulnerable;
    private const DebugProtectionBuffs NoAggro = DebugProtectionBuffs.NoAggro;
    private const DebugProtectionBuffs None = DebugProtectionBuffs.None;

    /// <summary>One character: what it wears, what ctx.Debug applied, who holds, and Core's grant.</summary>
    private sealed class Character
    {
        internal DebugProtectionBuffs Wearing;
        internal DebugProtectionBuffs Ours;
        internal DebugProtectionBuffs PendingDestroy;
        internal bool CoreGrant;
        internal bool Registered = true;
        internal bool Immortal = true;
        internal readonly Dictionary<string, DebugProtectionMode> Holders = new();
        internal int StrippedByUs;

        internal (bool ok, DebugProtectionBuffs after, bool pending, bool keptOther, DebugProtectionPlan plan) Call(
            string owner, DebugProtectionMode request)
        {
            if (request == DebugProtectionMode.None) Holders.Remove(owner);
            else Holders[owner] = request;
            var mode = DebugProtectionPolicy.Strongest(Holders.Values);

            var present = Wearing;
            var plan = DebugProtectionPolicy.Plan(mode, present, Ours, CoreGrant, Registered);
            Assert.Equal(None, plan.Remove & ~Ours);          // never removes what it did not apply
            if (CoreGrant) Assert.Equal(None, plan.Remove & Debug);

            var removed = plan.Remove & Wearing;
            PendingDestroy |= removed;
            StrippedByUs += System.Numerics.BitOperations.PopCount((uint)(removed & ~Ours));
            var applied = plan.Apply & ~Wearing;
            Wearing |= applied;                                // ApplyBuff is synchronous
            Ours = DebugProtectionPolicy.OursAfter(Ours, present, removed, applied);

            var after = DebugProtectionPolicy.After(Wearing, removed, None);
            const bool aggroable = true;
            bool ok = DebugProtectionPolicy.Ok(request, Immortal, after, aggroable, plan.Remove & ~removed);
            bool kept = request == DebugProtectionMode.AliveOnly && (after & (Debug | NoAggro)) != None;
            return (ok, after, removed != None, kept, plan);
        }

        /// <summary>Next frame: the destroys land.</summary>
        internal void Frame()
        {
            Wearing &= ~PendingDestroy;
            PendingDestroy = None;
        }
    }

    [Fact]
    public void The_smoke_sequence_leaves_Cores_debug_buff_on_and_reads_ok()
    {
        // .debug on (Core): Core applied the buff and holds the grant.
        var c = new Character { Wearing = Debug, CoreGrant = true };

        var on = c.Call("sample", DebugProtectionMode.Full);
        Assert.True(on.ok);
        Assert.Equal(None, on.plan.Apply);
        Assert.Equal(None, c.Ours);

        var alive = c.Call("sample", DebugProtectionMode.AliveOnly);
        c.Frame();
        Assert.Equal(None, alive.plan.Remove);               // the old KeepAlive destroyed it here
        Assert.Equal(Debug, alive.plan.KeptForeign);
        Assert.False(alive.plan.SetTargetable);              // Aggroable is not forced against Core's buff
        Assert.True(alive.ok);
        Assert.True(alive.keptOther);
        Assert.True(DebugProtectionPolicy.IgnoredByFoes(alive.after, aggroable: true));
        Assert.Equal(Debug, c.Wearing);

        var off = c.Call("sample", DebugProtectionMode.None);
        c.Frame();
        Assert.True(off.ok);
        Assert.Equal(None, off.plan.Remove);
        Assert.False(off.plan.MayClearImmortal);             // Core's Immortal stays Core's
        Assert.Equal(Debug, c.Wearing);
        Assert.Equal(0, c.StrippedByUs);
    }

    [Fact]
    public void Alone_ctx_Debug_swaps_its_own_debug_buff_for_invulnerability_and_reads_ok_the_same_frame()
    {
        var c = new Character();

        var on = c.Call("sample", DebugProtectionMode.Full);
        Assert.True(on.ok);
        Assert.Equal(Debug, c.Ours);

        var alive = c.Call("sample", DebugProtectionMode.AliveOnly);
        // Same frame: the debug buff is still in BuffBuffer, but the readback counts the destroy.
        Assert.Equal(Debug | Invuln, c.Wearing);
        Assert.Equal(Invuln, alive.after);
        Assert.True(alive.pending);
        Assert.True(alive.ok);                                // the old readback said ok=False here
        Assert.False(alive.keptOther);
        Assert.True(alive.plan.SetTargetable);
        c.Frame();
        Assert.Equal(Invuln, c.Wearing);
        Assert.Equal(Invuln, c.Ours);

        var off = c.Call("sample", DebugProtectionMode.None);
        Assert.True(off.ok);
        Assert.True(off.pending);
        Assert.Equal(None, off.after);
        Assert.True(off.plan.MayClearImmortal);
        c.Frame();
        Assert.Equal(None, c.Wearing);
        Assert.Equal(None, c.Ours);
    }

    [Fact]
    public void A_debug_buff_ctx_Debug_applied_becomes_Cores_once_Core_records_a_grant()
    {
        var c = new Character();
        c.Call("sample", DebugProtectionMode.Full);
        Assert.Equal(Debug, c.Ours);

        c.CoreGrant = true;                                   // .debug on adopts the existing buff

        var alive = c.Call("sample", DebugProtectionMode.AliveOnly);
        Assert.Equal(None, alive.plan.Remove);
        var off = c.Call("sample", DebugProtectionMode.None);
        c.Frame();
        Assert.Equal(None, off.plan.Remove);
        Assert.Equal(Debug, c.Wearing);
    }

    [Fact]
    public void An_admin_invulnerable_buff_that_was_already_on_is_never_removed()
    {
        var c = new Character { Wearing = Invuln, Registered = false };

        var alive = c.Call("sample", DebugProtectionMode.AliveOnly);
        Assert.Equal(None, alive.plan.Apply);
        Assert.True(alive.ok);

        var off = c.Call("sample", DebugProtectionMode.None);
        c.Frame();
        Assert.Equal(None, off.plan.Remove);
        Assert.Equal(Invuln, c.Wearing);
    }

    [Fact]
    public void A_keep_alive_holder_does_not_strip_what_another_holders_Protect_needs()
    {
        var c = new Character();
        c.Call("plugin-a", DebugProtectionMode.Full);

        var alive = c.Call("plugin-b", DebugProtectionMode.AliveOnly);
        c.Frame();
        Assert.Equal(None, alive.plan.Remove);
        Assert.True(alive.ok);
        Assert.True(alive.keptOther);
        Assert.Equal(Debug, c.Wearing);

        // plugin-a lets go: only the keep-alive request is left, so ctx.Debug's own debug buff
        // comes off and invulnerability goes on.
        var releaseA = c.Call("plugin-a", DebugProtectionMode.None);
        c.Frame();
        Assert.Equal(Debug, releaseA.plan.Remove);
        Assert.Equal(Invuln, releaseA.plan.Apply);
        Assert.Equal(Invuln, c.Wearing);

        c.Call("plugin-b", DebugProtectionMode.None);
        c.Frame();
        Assert.Equal(None, c.Wearing);
    }

    [Fact]
    public void Without_the_clone_Protect_uses_the_two_vanilla_buffs_and_keep_alive_drops_only_its_own_stealth()
    {
        var c = new Character { Registered = false };

        var on = c.Call("sample", DebugProtectionMode.Full);
        Assert.Equal(Invuln | NoAggro, on.plan.Apply);
        Assert.True(on.ok);

        var alive = c.Call("sample", DebugProtectionMode.AliveOnly);
        Assert.Equal(NoAggro, alive.plan.Remove);
        Assert.Equal(None, alive.plan.Apply);
        Assert.Equal(Invuln, alive.after);
        Assert.True(alive.ok);
    }

    [Fact]
    public void A_buff_someone_else_removed_and_reapplied_is_not_claimed_as_ours()
    {
        var c = new Character();
        c.Call("sample", DebugProtectionMode.Full);
        Assert.Equal(Debug, c.Ours);

        c.Wearing = None;                                     // removed elsewhere
        c.Call("sample", DebugProtectionMode.AliveOnly);      // ours forgets the lost Debug
        Assert.Equal(Invuln, c.Ours);
        c.Wearing |= Debug;                                   // someone else puts one on

        var off = c.Call("sample", DebugProtectionMode.None);
        Assert.Equal(Invuln, off.plan.Remove);
    }

    [Fact]
    public void Ok_judges_the_state_after_deferred_removals_land()
    {
        // KeepAlive: Immortal and invulnerable is the contract; targetable is reported, not required.
        Assert.True(DebugProtectionPolicy.Ok(DebugProtectionMode.AliveOnly, true, Invuln, aggroable: true, None));
        Assert.True(DebugProtectionPolicy.Ok(DebugProtectionMode.AliveOnly, true, Debug, aggroable: false, None));
        Assert.False(DebugProtectionPolicy.Ok(DebugProtectionMode.AliveOnly, false, Invuln, aggroable: true, None));
        Assert.False(DebugProtectionPolicy.Ok(DebugProtectionMode.AliveOnly, true, None, aggroable: true, None));

        Assert.True(DebugProtectionPolicy.Ok(DebugProtectionMode.Full, true, Debug, aggroable: true, None));
        Assert.True(DebugProtectionPolicy.Ok(DebugProtectionMode.Full, true, Invuln | NoAggro, aggroable: true, None));
        Assert.False(DebugProtectionPolicy.Ok(DebugProtectionMode.Full, true, Invuln, aggroable: true, None));

        Assert.True(DebugProtectionPolicy.Ok(DebugProtectionMode.None, false, Debug, aggroable: true, None));
        Assert.False(DebugProtectionPolicy.Ok(DebugProtectionMode.None, false, None, aggroable: true, Invuln));

        Assert.Equal(Invuln, DebugProtectionPolicy.After(Debug | Invuln, Debug, None));
        Assert.Equal(Debug | Invuln, DebugProtectionPolicy.After(Debug, None, Invuln));
    }

    [Fact]
    public void The_strongest_mode_wins()
    {
        Assert.Equal(DebugProtectionMode.None, DebugProtectionPolicy.Strongest(null));
        Assert.Equal(DebugProtectionMode.None, DebugProtectionPolicy.Strongest(new DebugProtectionMode[0]));
        Assert.Equal(DebugProtectionMode.Full, DebugProtectionPolicy.Strongest(new[] { DebugProtectionMode.AliveOnly, DebugProtectionMode.Full }));
        Assert.Equal(DebugProtectionMode.AliveOnly, DebugProtectionPolicy.Strongest(new[] { DebugProtectionMode.AliveOnly }));
    }

    [Fact]
    public void The_sample_reply_says_when_a_removal_is_pending_or_another_owners_buff_was_kept()
    {
        var kind = Progenitor.Sample.SampleDebugProtect.Kind.Alive;
        string plain = Progenitor.Sample.SampleDebugProtect.Reply(kind, true, true, true, false, true);
        Assert.DoesNotContain(Progenitor.Sample.SampleDebugProtect.PendingNote, plain);
        Assert.DoesNotContain(Progenitor.Sample.SampleDebugProtect.KeptOtherNote, plain);

        string both = Progenitor.Sample.SampleDebugProtect.Reply(kind, true, true, true, true, true,
            pending: true, keptOtherOwnersBuff: true);
        Assert.StartsWith("KeepAlive applied.", both);
        Assert.Contains(Progenitor.Sample.SampleDebugProtect.PendingNote, both);
        Assert.Contains(Progenitor.Sample.SampleDebugProtect.KeptOtherNote, both);
    }
}
