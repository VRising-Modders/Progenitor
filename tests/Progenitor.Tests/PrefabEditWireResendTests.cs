using System;
using System.Collections.Generic;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// The <c>PrfE</c> resend after a <b>client-side</b> Core swap (job FEDW4, R52 finding 3).
///
/// <para><b>Why the mechanism FEDW3 built cannot fire.</b> It watched the edge into
/// <c>Handshake.UserAnswerStatus(index) == "ok"</c>, on the premise that a Core swap re-runs the
/// handshake. A <em>client</em> swap does not move the server's answer status: the Install re-arm
/// is server-only, <c>HandshakeAnswerWatch.Arm</c> returns early for a user already at <c>Ok</c>,
/// and <c>OnAnswer</c> writes <c>Ok</c> over <c>Ok</c>. So the edge fires once per user per
/// <em>server</em> generation and the client that swapped keeps its own editors forever. The
/// recovery is an explicit seam, <c>PrefabRegistry.ResendEditLayersTo(userIndex)</c>, called from
/// the answer path when an already-ok user re-answers — and the send is deduped per user so the
/// seam, the join send and the surviving poll cannot duplicate each other.</para>
/// </summary>
public class PrefabEditWireResendTests
{
    private const int Guid = 480926629;

    // ---- the per-user record --------------------------------------------------------------------

    [Fact]
    public void A_user_is_not_sent_the_same_layer_twice()
    {
        var layer = new PrefabEditWireLayer();
        Assert.True(layer.WouldSendToUser(1, Guid, 0xABC));

        layer.RecordSentToUser(1, Guid, 0xABC);
        Assert.False(layer.WouldSendToUser(1, Guid, 0xABC));

        // A changed layer is a new payload, and another user has seen nothing.
        Assert.True(layer.WouldSendToUser(1, Guid, 0xDEF));
        Assert.True(layer.WouldSendToUser(2, Guid, 0xABC));
    }

    /// <summary>
    /// The record is per user, so it cannot be confused with the broadcast dedupe: a layer the
    /// server has broadcast is still owed to a user who never received it.
    /// </summary>
    [Fact]
    public void The_per_user_record_is_independent_of_the_broadcast_dedupe()
    {
        var layer = new PrefabEditWireLayer();
        layer.RecordSent(Guid, 0xABC, nowMs: 1000);
        Assert.False(layer.WouldSend(Guid, 0xABC));
        Assert.True(layer.WouldSendToUser(7, Guid, 0xABC));
    }

    [Fact]
    public void A_fresh_join_forgets_what_the_previous_occupant_of_that_index_had()
    {
        var layer = new PrefabEditWireLayer();
        layer.RecordSentToUser(3, Guid, 0xABC);
        layer.ForgetUser(3);
        Assert.True(layer.WouldSendToUser(3, Guid, 0xABC));
        Assert.Equal(0, layer.TrackedUserCount);
    }

    [Fact]
    public void The_records_of_users_that_are_gone_are_dropped()
    {
        var layer = new PrefabEditWireLayer();
        layer.RecordSentToUser(1, Guid, 1);
        layer.RecordSentToUser(2, Guid, 1);
        layer.RetainUsers(new HashSet<int> { 2 });
        Assert.True(layer.WouldSendToUser(1, Guid, 1));
        Assert.False(layer.WouldSendToUser(2, Guid, 1));

        layer.RetainUsers(Array.Empty<int>());
        Assert.Equal(0, layer.TrackedUserCount);
    }

    [Fact]
    public void Dropping_a_prefab_drops_what_every_user_was_sent_for_it()
    {
        var layer = new PrefabEditWireLayer();
        layer.RecordSentToUser(1, Guid, 1);
        layer.RecordSentToUser(1, 99, 1);
        layer.DropGuid(Guid);
        Assert.True(layer.WouldSendToUser(1, Guid, 1));
        Assert.False(layer.WouldSendToUser(1, 99, 1));
    }

    [Fact]
    public void A_generation_swap_forgets_every_per_user_record()
    {
        var layer = new PrefabEditWireLayer();
        layer.RecordSentToUser(1, Guid, 1);
        layer.ClearOwned();
        Assert.Equal(0, layer.TrackedUserCount);

        layer.RecordSentToUser(1, Guid, 1);
        layer.Clear();
        Assert.Equal(0, layer.TrackedUserCount);
    }

    // ---- the seam, in the live code -------------------------------------------------------------

    /// <summary>
    /// The seam exists, is public, and is what the <c>Networking</c> side will call. Pinned
    /// because nothing in <c>Content</c> calls it — the caller is in another module and another
    /// job (FMAN21/22), so a rename here would look unused and be deleted.
    /// </summary>
    [Fact]
    public void The_resend_seam_is_public_and_goes_to_the_wire()
    {
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        SourcePin.AssertStatement(registry, "public static void ResendEditLayersTo(int userIndex)");

        string body = SourcePin.MethodBody(registry, "public static void ResendEditLayersTo(int userIndex)");
        SourcePin.AssertUncommented(body, "PrefabEditWireApply.ResendToUserIndex(TargetWorld(), Edits, userIndex)");
    }

    /// <summary>
    /// The join send says it is a join (which clears the per-user record, because a user index is
    /// reused), and the resend does not — that is the whole of "the join is not sent twice".
    /// </summary>
    [Fact]
    public void The_join_send_resets_the_per_user_record_and_the_resend_does_not()
    {
        string registry = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabRegistry.cs");
        SourcePin.AssertUncommented(
            registry,
            "PrefabEditWireApply.SendAllToUser(ev.World, Edits, ev.UserEntity, ev.CharacterName, freshJoin: true)");

        string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
        string send = SourcePin.MethodBody(apply, "internal static int SendAllToUser(");
        SourcePin.AssertStatement(send, "if (freshJoin) edits.Wire.ForgetUser(user.Index);");
        SourcePin.AssertUncommented(send, "edits.Wire.WouldSendToUser(user.Index, guids[i], hash)");
        SourcePin.AssertStatement(send, "edits.Wire.RecordSentToUser(user.Index, guids[i], hash);");
        // The rate floor and the broadcast dedupe belong to the broadcast, not to one user.
        SourcePin.AssertStatement(send, "if (freshJoin) edits.Wire.RecordSent(guids[i], hash, now);");

        string resend = SourcePin.MethodBody(apply, "internal static int ResendToUserIndex(");
        SourcePin.AssertUncommented(resend, "SendAllToUser(server, edits, users[i], \"user \" + userIndex)");
    }

    [Fact]
    public void The_poll_forgets_a_user_that_has_gone()
    {
        string apply = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireApply.cs");
        string poll = SourcePin.MethodBody(apply, "internal static int PollHandshakeResends(");
        SourcePin.AssertStatement(poll, "edits.Wire.RetainUsers(connected);");
    }

    /// <summary>
    /// The two files used to assert opposite things about whether a received layer survives a Core
    /// swap. It does not — the table is a Core static — and the resend is the recovery. Pinned
    /// negatively, because the wrong sentence is what a mechanism was built on top of.
    /// </summary>
    [Fact]
    public void Nothing_claims_the_received_layer_survives_a_core_swap()
    {
        string layer = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditWireLayer.cs");
        Assert.Contains("does not survive a Core swap", layer);
        Assert.DoesNotContain("A received server layer is kept", layer);

        string table = SourcePin.ReadFile("src", "Progenitor.Core", "Content", "PrefabEditTable.cs");
        Assert.DoesNotContain("the client re-applies it after the next pass", table);
    }
}
