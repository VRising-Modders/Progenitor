using System;
using System.Collections.Generic;
using System.Text;
using Progenitor.Chat;
using Xunit;

namespace Progenitor.Tests;

/// <summary>CHAT2: the names chat name completion offers (the game's player list, the nearby fallback), game-free.</summary>
public sealed class ChatRosterTests
{
    private static void WriteEntry(byte[] buffer, int index, string name, byte connected, int nameLength = -1)
    {
        int at = index * ChatRoster.Stride;
        byte[] utf8 = Encoding.UTF8.GetBytes(name);
        int length = nameLength >= 0 ? nameLength : utf8.Length;
        buffer[at + ChatRoster.NameOffset] = (byte)length;
        buffer[at + ChatRoster.NameOffset + 1] = (byte)(length >> 8);
        Array.Copy(utf8, 0, buffer, at + ChatRoster.NameOffset + 2, Math.Min(utf8.Length, 62));
        // Platform id, user index and network id sit between the name and the flag: fill them so a wrong offset shows.
        for (int i = 0x40; i < ChatRoster.IsConnectedOffset; i++) buffer[at + i] = 0xAB;
        buffer[at + ChatRoster.IsConnectedOffset] = connected;
    }

    private static byte[] Buffer(params (string Name, byte Connected)[] rows)
    {
        var bytes = new byte[rows.Length * ChatRoster.Stride];
        for (int i = 0; i < rows.Length; i++) WriteEntry(bytes, i, rows[i].Name, rows[i].Connected);
        return bytes;
    }

    [Fact]
    public void The_layout_is_the_metadatas()
    {
        Assert.Equal(96, ChatRoster.Stride);
        Assert.Equal(0x0, ChatRoster.NameOffset);
        Assert.Equal(0x5C, ChatRoster.IsConnectedOffset);
        Assert.True(ChatRoster.NameOffset + ChatRoster.FixedString64Bytes <= ChatRoster.IsConnectedOffset);
    }

    [Fact]
    public void Only_connected_names_are_read_in_list_order()
    {
        var bytes = Buffer(("Guide", 1), ("Gone", 0), ("Visitor", 1), ("", 1));
        Assert.Equal(new[] { "Guide", "Visitor" }, ChatRoster.DecodeConnectedNames(bytes, 4));
    }

    [Fact]
    public void Names_in_any_script_decode()
    {
        var bytes = Buffer(("Zoë", 1), ("ヴァンパイア", 1));
        Assert.Equal(new[] { "Zoë", "ヴァンパイア" }, ChatRoster.DecodeConnectedNames(bytes, 2));
    }

    [Fact]
    public void An_empty_list_reads_as_empty() =>
        Assert.Empty(ChatRoster.DecodeConnectedNames(Array.Empty<byte>(), 0)!);

    [Theory]
    [InlineData(-1)]
    [InlineData(ChatRoster.MaxEntries + 1)]
    [InlineData(int.MaxValue)]
    public void A_length_out_of_bounds_is_refused(int length)
    {
        Assert.False(ChatRoster.LengthOk(length));
        Assert.Null(ChatRoster.DecodeConnectedNames(new byte[ChatRoster.Stride], length));
    }

    [Fact]
    public void Bytes_shorter_than_the_length_claims_are_refused() =>
        Assert.Null(ChatRoster.DecodeConnectedNames(Buffer(("Guide", 1)), 2));

    [Fact]
    public void A_bad_connected_flag_refuses_the_whole_list()
    {
        var bytes = Buffer(("Guide", 1), ("Visitor", 1));
        bytes[ChatRoster.Stride + ChatRoster.IsConnectedOffset] = 7;
        Assert.Null(ChatRoster.DecodeConnectedNames(bytes, 2));
    }

    [Fact]
    public void A_name_length_past_the_capacity_refuses_the_whole_list()
    {
        var bytes = Buffer(("Guide", 1));
        WriteEntry(bytes, 0, "Guide", 1, nameLength: ChatRoster.FixedString64Capacity + 1);
        Assert.Null(ChatRoster.DecodeConnectedNames(bytes, 1));
    }

    [Fact]
    public void Invalid_utf8_refuses_the_whole_list()
    {
        var bytes = Buffer(("Guide", 1));
        bytes[ChatRoster.NameOffset + 2] = 0xFF;
        Assert.Null(ChatRoster.DecodeConnectedNames(bytes, 1));
    }

    [Fact]
    public void A_name_at_the_capacity_decodes()
    {
        string name = new('a', ChatRoster.FixedString64Capacity);
        Assert.Equal(new[] { name }, ChatRoster.DecodeConnectedNames(Buffer((name, 1)), 1));
    }

    [Fact]
    public void The_game_list_is_complete_and_the_fallback_is_not_used()
    {
        var merged = ChatRoster.Merge(new[] { "Guide", "Visitor" }, new[] { "Guide" }, new[] { "Stranger" }, null);
        Assert.True(merged.Complete);
        Assert.Equal(new[] { "Guide", "Visitor" }, merged.Names);
    }

    [Fact]
    public void Without_the_game_list_local_and_nearby_names_merge_and_may_be_incomplete()
    {
        var merged = ChatRoster.Merge(null, new[] { "Guide" }, new[] { "guide", "Visitor", " ", "Visitor" }, null);
        Assert.False(merged.Complete);
        Assert.Equal(new[] { "Guide", "Visitor" }, merged.Names);
    }

    [Fact]
    public void Test_names_are_added_once_either_way()
    {
        var names = ChatRoster.ParseTestNames(" Mira, visitor ;;Tov ");
        Assert.Equal(new[] { "Mira", "visitor", "Tov" }, names);
        Assert.Equal(new[] { "Visitor", "Mira", "Tov" }, ChatRoster.Merge(new[] { "Visitor" }, null, null, names).Names);
        Assert.Equal(new[] { "Mira", "visitor", "Tov" }, ChatRoster.Merge(null, null, null, names).Names);
        Assert.Empty(ChatRoster.ParseTestNames(""));
        Assert.Empty(ChatRoster.ParseTestNames(null));
    }

    [Fact]
    public void An_empty_game_list_is_still_complete()
    {
        var merged = ChatRoster.Merge(new List<string>(), new[] { "Guide" }, new[] { "Visitor" }, null);
        Assert.True(merged.Complete);
        Assert.Empty(merged.Names);
    }

    [Fact]
    public void Completion_finds_a_player_from_the_list_by_prefix_ignoring_case()
    {
        // The live bug: Guide and Visitor online, the client world holds only Guide's User.
        var roster = ChatRoster.DecodeConnectedNames(Buffer(("Guide", 1), ("Visitor", 1)), 2);
        var online = ChatRoster.Merge(roster, new[] { "Guide" }, null, null).Names;
        var completer = new ChatNameCompleter();
        Assert.Equal("/w Visitor ", completer.Complete("/w Vis", online, null));
        completer.Reset();
        Assert.Equal("/w Visitor ", completer.Complete("/w vIS", online, null));
        completer.Reset();
        Assert.True(completer.WouldComplete("/w g", online, null));
        Assert.Equal(new[] { "Guide", "Visitor" }, ChatCommandLogic.Matches("", online, null));
    }

    [Fact]
    public void The_no_match_line_states_a_fact_only_with_the_game_list()
    {
        Assert.Equal("No player online named \"Vis\".", ChatRoster.NoMatchText("Vis", complete: true));
        string partial = ChatRoster.NoMatchText("Vis", complete: false);
        Assert.DoesNotContain("online named", partial);
        Assert.Contains("\"Vis\"", partial);
        Assert.Contains("may be incomplete", partial);
    }
}
