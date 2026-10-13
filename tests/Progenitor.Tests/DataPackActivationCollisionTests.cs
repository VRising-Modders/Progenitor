using System;
using System.Collections.Generic;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public sealed class DataPackActivationCollisionTests
{
    [Fact]
    public void Owner_claim_after_preflight_refuses_step_and_leaves_untouched_claim_during_rollback()
    {
        var owner = new object();
        var outsider = new object();
        var proposed = Enumerable.Range(1, 9).Select(i => new DataPackItemIdentity("item" + i, i)).ToArray();
        var registered = new List<RegisteredItemIdentity>();
        var touched = new HashSet<int>();
        Assert.True(DataPackRegistrationRules.TryValidate(proposed, registered, owner, out _));
        var job = new DataPackActivation(9, index =>
        {
            var item = proposed[index];
            if (!DataPackRegistrationRules.TryValidate(new[] { item }, registered, owner, out var error))
                throw new InvalidOperationException(error);
            touched.Add(item.Guid);
            registered.Add(new(item.Id, item.Guid, owner));
        }, index =>
        {
            var item = proposed[index];
            if (!touched.Remove(item.Guid)) return;
            registered.RemoveAll(row => row.Guid == item.Guid && ReferenceEquals(row.Owner, owner));
        });
        job.Advance(() => 0);
        Assert.False(job.Complete);
        registered.Add(new(proposed[8].Id, proposed[8].Guid, outsider));
        job.Advance(() => 0, synchronous: true);
        Assert.True(job.Complete);
        Assert.NotNull(job.Failure);
        Assert.Null(job.RollbackFailure);
        var surviving = Assert.Single(registered);
        Assert.Same(outsider, surviving.Owner);
        Assert.Equal(9, surviving.Guid);
    }

    [Fact]
    public void Rollback_cannot_restore_old_pack_over_a_later_unrelated_claim()
    {
        var packOwner = new object();
        var outsider = new object();
        var oldItem = new DataPackItemIdentity("old-item", 123);
        var registration = new RegisteredItemIdentity(oldItem.Id, oldItem.Guid, outsider);
        Assert.False(DataPackRegistrationRules.TryValidate(new[] { oldItem },
            new[] { registration }, packOwner, out var error));
        Assert.Contains("already owned", error);
        Assert.Same(outsider, registration.Owner);
    }

    [Fact]
    public void Admission_recheck_refuses_new_item_when_another_registration_consumed_last_slot()
    {
        var item = new DataPackItemIdentity("pack.item", 2);
        var none = Array.Empty<DataPackItemIdentity>();
        Assert.True(DataPackAdmissionRules.TryValidate(new[] { item }, none, none,
            Array.Empty<KeyLedgerRow>(), 100, 101, out _));
        var competing = new[] { new DataPackItemIdentity("other.item", 1) };
        Assert.False(DataPackAdmissionRules.TryValidate(new[] { item }, competing, competing,
            Array.Empty<KeyLedgerRow>(), 100, 101, out var error));
        Assert.Contains("unsynchronizable", error, StringComparison.OrdinalIgnoreCase);
    }
}
