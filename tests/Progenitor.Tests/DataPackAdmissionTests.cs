using System;
using System.Linq;
using Progenitor.Content;
using Xunit;

namespace Progenitor.Tests;

public class DataPackAdmissionTests
{
    [Theory]
    [InlineData(256, false)]
    [InlineData(512, false)]
    [InlineData(1024, true)]
    public void Extended_capacity_is_only_advertised_when_client_enables_it(int limit, bool advertised)
    {
        var caps = Progenitor.Networking.Handshake.CapabilitiesForPrefabLimit(limit);
        Assert.Equal(advertised, (caps & Progenitor.Networking.Handshake.Capability.ItemCapacity1024) != 0);
    }

    [Theory]
    [InlineData(500, true)]
    [InlineData(965, true)]
    [InlineData(966, false)]
    public void Raised_identity_budget_still_respects_native_key_capacity(int count, bool accepted)
    {
        Assert.Equal(accepted, DataPackAdmissionRules.TryValidate(Items(count, longIds: true),
            Array.Empty<DataPackItemIdentity>(), Array.Empty<DataPackItemIdentity>(),
            Array.Empty<KeyLedgerRow>(), 1083, 2048, out var reason, maxIdentities: 1024));
        if (!accepted) Assert.Contains("capacity", reason);
    }

    private static DataPackItemIdentity[] Items(int count, int first = 1, bool longIds = false) =>
        Enumerable.Range(first, count).Select(i => new DataPackItemIdentity(
            longIds ? new string('a', 57) + i.ToString("D6") : "item." + i, i)).ToArray();
    private static KeyLedgerRow[] Rows(DataPackItemIdentity[] items) =>
        items.Select(i => new KeyLedgerRow(i.Guid, i.Id, "", true)).ToArray();
    private static bool Admit(DataPackItemIdentity[] proposed, DataPackItemIdentity[] known,
        KeyLedgerRow[] historical, out string reason, DataPackItemIdentity[]? pending = null, int capacity = 10000) =>
        DataPackAdmissionRules.TryValidate(proposed, known, pending ?? Array.Empty<DataPackItemIdentity>(),
            historical, 100, capacity, out reason);

    [Theory]
    [InlineData(247, true)]
    [InlineData(248, false)]
    public void Eight_baseline_definitions_and_one_retired_item_reserve_nine_identities(int count, bool accepted)
    {
        Assert.Equal(accepted, Admit(Items(count, 10), Items(8), Rows(Items(1, 9)), out var reason));
        if (!accepted) Assert.Contains("257 distinct", reason);
    }

    [Fact]
    public void Updating_or_reinstalling_known_items_does_not_consume_another_identity_or_key_row()
    {
        var all = Items(256);
        Assert.True(Admit(all, all, Rows(all), out var reason, all), reason);
    }

    [Theory]
    [InlineData(225, true)]
    [InlineData(226, true)]
    public void Chunked_ledger_allows_long_ids_beyond_one_transport_packet(int count, bool accepted)
    {
        Assert.Equal(accepted, Admit(Items(count, longIds: true), Array.Empty<DataPackItemIdentity>(),
            Array.Empty<KeyLedgerRow>(), out var reason));
        if (!accepted) Assert.Contains("ledger unsynchronizable", reason);
    }

    [Fact]
    public void Retired_long_ids_fit_chunked_ledger()
    {
        Assert.True(Admit(Items(1, 226, true), Array.Empty<DataPackItemIdentity>(),
            Rows(Items(225, longIds: true)), out var reason));
        Assert.Empty(reason);
    }

    [Fact]
    public void Pending_long_ids_fit_chunked_ledger()
    {
        Assert.True(Admit(Items(1, 226, true), Items(225, longIds: true),
            Array.Empty<KeyLedgerRow>(), out var reason, Items(225, longIds: true)));
        Assert.Empty(reason);
    }

    [Fact]
    public void Pending_key_appends_reserve_native_capacity()
    {
        Assert.False(Admit(Items(1, 3), Items(2), Array.Empty<KeyLedgerRow>(),
            out _, Items(2), capacity: 102));
    }

    [Fact]
    public void Historical_rows_remain_reserved_even_without_active_definitions()
    {
        Assert.False(Admit(Items(1, 257), Array.Empty<DataPackItemIdentity>(), Rows(Items(256)), out var reason));
        Assert.Contains("257 distinct", reason);
    }

    [Fact]
    public void Reusing_a_historical_row_projects_the_new_wire_id_length()
    {
        var historical = Rows(Items(225, longIds: true).Concat(Items(1, 226)).ToArray());
        Assert.True(Admit(Items(1, 226, true), Array.Empty<DataPackItemIdentity>(), historical, out var reason));
        Assert.Empty(reason);
    }
}
