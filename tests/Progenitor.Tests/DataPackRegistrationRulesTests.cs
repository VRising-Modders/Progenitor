using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class DataPackRegistrationRulesTests
{
    [Fact]
    public void Different_id_with_same_hash_cannot_replace_another_owner()
    {
        Assert.False(DataPackRegistrationRules.TryValidate(new[] { new DataPackItemIdentity("pack.item", 123) },
            new[] { new RegisteredItemIdentity("another.item", 123, new object()) }, null, out _));
    }

    [Fact]
    public void Proposed_items_cannot_share_a_hash()
    {
        Assert.False(DataPackRegistrationRules.TryValidate(new[] {
            new DataPackItemIdentity("pack.a", 123), new DataPackItemIdentity("pack.b", 123)
        }, System.Array.Empty<RegisteredItemIdentity>(), null, out _));
    }

    [Fact]
    public void Existing_pack_may_update_its_exact_item_identity_and_add_new_items()
    {
        var owner = new object();
        Assert.True(DataPackRegistrationRules.TryValidate(new[] {
            new DataPackItemIdentity("pack.a", 123), new DataPackItemIdentity("pack.b", 124)
        }, new[] { new RegisteredItemIdentity("pack.a", 123, owner) }, owner, out _));
    }

    [Fact]
    public void Own_old_definition_with_different_id_still_refuses_hash_collision()
    {
        var owner = new object();
        Assert.False(DataPackRegistrationRules.TryValidate(new[] { new DataPackItemIdentity("pack.new", 123) },
            new[] { new RegisteredItemIdentity("pack.old", 123, owner) }, owner, out _));
    }

    [Fact]
    public void Exact_id_owned_by_someone_else_is_refused()
    {
        Assert.False(DataPackRegistrationRules.TryValidate(new[] { new DataPackItemIdentity("pack.item", 123) },
            new[] { new RegisteredItemIdentity("pack.item", 123, new object()) }, new object(), out _));
    }

    [Fact]
    public void Standalone_edit_reserves_identity_without_consuming_an_item_key_row()
    {
        var known = new System.Collections.Generic.List<DataPackItemIdentity>();
        for (int i = 1; i <= 255; i++)
            known.Add(new DataPackItemIdentity("", i)); // Standalone native PrfE targets.
        var proposed = new[] { new DataPackItemIdentity("pack.sword", 1000) };
        var noItems = System.Array.Empty<DataPackItemIdentity>();
        var noRows = System.Array.Empty<KeyLedgerRow>();
        // Only the proposed sword needs the one available item-key row.
        Assert.True(DataPackAdmissionRules.TryValidate(proposed, known, noItems, noRows,
            100, 101, out _));
        known.Add(new DataPackItemIdentity("", 256));
        Assert.False(DataPackAdmissionRules.TryValidate(proposed, known, noItems, noRows,
            100, 101, out var error));
        Assert.Contains("257 distinct client prefab identities", error);
        // A reader for an already counted identity is not a second reservation.
        known.RemoveAt(known.Count - 1);
        known.Add(new DataPackItemIdentity("", 1));
        Assert.True(DataPackAdmissionRules.TryValidate(proposed, known, noItems, noRows,
            100, 101, out _));
    }

    [Fact]
    public void Zero_hash_is_not_a_prefab_identity()
    {
        Assert.False(DataPackRegistrationRules.TryValidate(new[] { new DataPackItemIdentity("pack.item", 0) },
            System.Array.Empty<RegisteredItemIdentity>(), null, out _));
    }
}
