using System;
using System.Collections.Generic;
using Progenitor.Players;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// Game-free coverage of the A3 player directory: name/account lookup, ambiguous names,
/// online filter, freshness (epoch + revision), rename detection, and invalidate reasons.
/// Linked through PlayerDirectoryLogic / PlayerDirectoryCache / PlayerInfo — no game.
/// </summary>
public class PlayerDirectoryTests
{
    private static PlayerDirectoryRow Row(
        ulong account,
        string name,
        bool online = true,
        int userIndex = 1,
        int userVersion = 1,
        int slot = 0) =>
        new(account, name, online, online ? slot : -1, userIndex, userVersion, userIndex + 100, userVersion);

    [Fact]
    public void Account_zero_is_not_a_stable_identity()
    {
        Assert.False(PlayerDirectoryLogic.IsValidAccount(0));
        Assert.True(PlayerAccountId.None.IsNone);
        Assert.False(PlayerAccountId.From(76561198000000000UL).IsNone);
    }

    [Fact]
    public void Names_match_whole_and_case_insensitive()
    {
        Assert.True(PlayerDirectoryLogic.NamesMatch("Bob", "bob"));
        Assert.False(PlayerDirectoryLogic.NamesMatch("Bob", "Bobby"));
        Assert.False(PlayerDirectoryLogic.NamesMatch("", "Bob"));
        Assert.False(PlayerDirectoryLogic.NamesMatch(null, "Bob"));
    }

    [Fact]
    public void CollectOnline_skips_offline_and_zero_account()
    {
        var rows = new List<PlayerDirectoryRow>
        {
            Row(1, "A", online: true),
            Row(2, "B", online: false),
            Row(0, "Ghost", online: true),
            Row(3, "C", online: true),
        };
        var dest = new List<PlayerDirectoryRow>();
        PlayerDirectoryLogic.CollectOnline(rows, dest);
        Assert.Equal(2, dest.Count);
        Assert.Equal(1UL, dest[0].AccountId);
        Assert.Equal(3UL, dest[1].AccountId);
    }

    [Fact]
    public void Exact_name_match_returns_one()
    {
        var rows = new[] { Row(1, "Alice"), Row(2, "Bob") };
        var hits = new List<int>();
        var status = PlayerDirectoryLogic.MatchByName(rows, "bob", onlineOnly: true, hits);
        Assert.Equal(PlayerLookupStatus.Exact, status);
        Assert.Equal(new[] { 1 }, hits);
    }

    [Fact]
    public void Duplicate_names_are_ambiguous_not_arbitrary()
    {
        var rows = new[]
        {
            Row(10, "Twin", online: true, userIndex: 1),
            Row(11, "twin", online: true, userIndex: 2),
            Row(12, "Other", online: true, userIndex: 3),
        };
        var hits = new List<int>();
        var status = PlayerDirectoryLogic.MatchByName(rows, "Twin", onlineOnly: true, hits);
        Assert.Equal(PlayerLookupStatus.Ambiguous, status);
        Assert.Equal(new[] { 0, 1 }, hits);

        var lookup = PlayerDirectoryLogic.ToNameLookup(status, rows, hits, worldEpoch: 5, directoryRevision: 2, isFresh: true);
        Assert.Equal(PlayerLookupStatus.Ambiguous, lookup.Status);
        Assert.Null(lookup.Player);
        Assert.Equal(2, lookup.Candidates.Count);
        Assert.True(lookup.IsFresh);
    }

    [Fact]
    public void OnlineOnly_name_lookup_ignores_offline_duplicate()
    {
        var rows = new[]
        {
            Row(10, "Sam", online: true),
            Row(11, "Sam", online: false),
        };
        var hits = new List<int>();
        Assert.Equal(PlayerLookupStatus.Exact, PlayerDirectoryLogic.MatchByName(rows, "Sam", onlineOnly: true, hits));
        Assert.Equal(new[] { 0 }, hits);

        hits.Clear();
        Assert.Equal(PlayerLookupStatus.Ambiguous, PlayerDirectoryLogic.MatchByName(rows, "Sam", onlineOnly: false, hits));
        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public void Blank_name_is_not_found()
    {
        var rows = new[] { Row(1, "A") };
        var hits = new List<int>();
        Assert.Equal(PlayerLookupStatus.NotFound, PlayerDirectoryLogic.MatchByName(rows, "  ", onlineOnly: true, hits));
        Assert.Empty(hits);
    }

    [Fact]
    public void Account_lookup_honors_onlineOnly()
    {
        var rows = new[] { Row(99, "Offline", online: false) };
        Assert.Equal(0, PlayerDirectoryLogic.FindByAccount(rows, 99, onlineOnly: false));
        Assert.Equal(-1, PlayerDirectoryLogic.FindByAccount(rows, 99, onlineOnly: true));
        Assert.Equal(-1, PlayerDirectoryLogic.FindByAccount(rows, 0, onlineOnly: false));
        Assert.Equal(-1, PlayerDirectoryLogic.FindByAccount(rows, 42, onlineOnly: false));
    }

    [Fact]
    public void Freshness_requires_matching_epoch_and_revision()
    {
        Assert.True(PlayerDirectoryLogic.IsFresh(3, 4, 3, 4));
        Assert.False(PlayerDirectoryLogic.IsFresh(3, 4, 4, 4)); // world moved
        Assert.False(PlayerDirectoryLogic.IsFresh(3, 4, 3, 5)); // revision bumped
        Assert.False(PlayerDirectoryLogic.IsFresh(0, 1, 0, 1)); // epoch 0 never fresh
        Assert.False(PlayerDirectoryLogic.IsFresh(1, 0, 1, 0)); // revision 0 never fresh
    }

    [Fact]
    public void Invalidate_bumps_revision_and_clears_rows()
    {
        var cache = new PlayerDirectoryCache();
        cache.Replace(10, new[] { Row(1, "A") });
        var before = cache.Revision;
        Assert.Equal(1, cache.Count);

        cache.Invalidate(PlayerDirectoryLogic.ReasonDisconnect);
        Assert.Equal(before + 1, cache.Revision);
        Assert.Equal(0, cache.Count);
        Assert.Equal(PlayerDirectoryLogic.ReasonDisconnect, cache.LastInvalidateReason);

        var snap = cache.Capture();
        Assert.False(snap.IsFresh(10, cache.Revision)); // epoch cleared to 0
    }

    [Fact]
    public void Rename_on_replace_bumps_revision()
    {
        var cache = new PlayerDirectoryCache();
        cache.Replace(1, new[] { Row(5, "OldName") });
        int rev = cache.Revision;
        cache.Replace(1, new[] { Row(5, "NewName") });
        Assert.True(cache.Revision > rev);
        Assert.Equal(PlayerDirectoryLogic.ReasonRename, cache.LastInvalidateReason);
        Assert.Equal("NewName", cache.Capture().Rows[0].DisplayName);
    }

    [Fact]
    public void Same_name_replace_does_not_count_as_rename()
    {
        var cache = new PlayerDirectoryCache();
        cache.Replace(1, new[] { Row(5, "Same") });
        int rev = cache.Revision;
        cache.Replace(2, new[] { Row(5, "same") }); // case-insensitive same
        Assert.Equal(rev, cache.Revision);
        Assert.Equal(2, cache.Epoch);
    }

    [Fact]
    public void DetectedRename_ignores_new_accounts_and_empty_names()
    {
        Assert.False(PlayerDirectoryLogic.DetectedRename(
            new[] { Row(1, "A") },
            new[] { Row(1, "A"), Row(2, "B") }));
        Assert.False(PlayerDirectoryLogic.DetectedRename(
            new[] { Row(1, "") },
            new[] { Row(1, "NowNamed") }));
        Assert.True(PlayerDirectoryLogic.DetectedRename(
            new[] { Row(1, "Before") },
            new[] { Row(1, "After") }));
    }

    [Fact]
    public void Row_ToInfo_stamps_epoch_and_does_not_treat_entity_as_identity()
    {
        var row = Row(777, "Rowan", online: true, userIndex: 42, userVersion: 3, slot: 7);
        var info = row.ToInfo(worldEpoch: 9, directoryRevision: 4);
        Assert.Equal(777UL, info.PlatformId);
        Assert.Equal("Rowan", info.DisplayName);
        Assert.True(info.IsOnline);
        Assert.Equal(42, info.Session.UserIndex);
        Assert.Equal(3, info.Session.UserVersion);
        Assert.Equal(7, info.Session.ConnectedUserIndex);
        Assert.Equal(9, info.WorldEpoch);
        Assert.Equal(4, info.DirectoryRevision);
        // Stable identity string is the account id, not index:version
        Assert.Equal("777", info.Account.ToString());
        Assert.DoesNotContain("42:3", info.Account.ToString());
    }

    [Fact]
    public void Connect_disconnect_world_reload_reasons_are_stable_tokens()
    {
        Assert.Equal("connect", PlayerDirectoryLogic.ReasonConnect);
        Assert.Equal("disconnect", PlayerDirectoryLogic.ReasonDisconnect);
        Assert.Equal("rename", PlayerDirectoryLogic.ReasonRename);
        Assert.Equal("world", PlayerDirectoryLogic.ReasonWorld);
        Assert.Equal("reload", PlayerDirectoryLogic.ReasonReload);
    }
}
