using System;
using System.Collections.Generic;
using Progenitor.Networking;
using Xunit;

namespace Progenitor.Tests;

/// <summary>
/// NODESCCHAT (docs/server-list-description.md, "The description in chat"): the join-chat guard over a fake of the
/// game's <c>ServerBootstrapSystem.OnUserConnected</c>, which (native 2026-10-06) sends the live
/// <c>ServerHostSettings.Description</c> as a System line when it is not empty. The fake also sends another System
/// line that does not read the description, standing for every other message on the same path.
/// </summary>
public class JoinDescriptionChatTests
{
    private const string Description = "Hardcore PvP\nRequires the Progenitor mod (thunderstore.io)";
    private const string OtherMessage = "An admin announcement";

    private sealed class Field : IJoinDescriptionField
    {
        public string? Value;
        public int Writes;
        public string? Read() => Value;
        public void Write(string text) { Value = text; Writes++; }
    }

    /// <summary>The game's join, as the analyze reads: other messages, then the description if non-empty.</summary>
    private static List<string> GameJoin(Field field, Action? midJoin = null)
    {
        var sent = new List<string> { OtherMessage };
        midJoin?.Invoke();
        string? description = field.Read();
        if (!string.IsNullOrEmpty(description)) sent.Add(description!);
        return sent;
    }

    private static List<string> Join(JoinDescriptionChatGuard guard, Field field, bool descriptionInChat)
    {
        guard.Before(field, descriptionInChat);
        var sent = GameJoin(field);
        guard.Restore(field);
        return sent;
    }

    [Fact]
    public void The_description_is_not_sent_on_join_and_is_put_back_after()
    {
        var field = new Field { Value = Description };
        var guard = new JoinDescriptionChatGuard();

        var sent = Join(guard, field, descriptionInChat: false);

        Assert.Equal(new[] { OtherMessage }, sent);
        Assert.Equal(Description, field.Value);
        Assert.False(guard.Pending);
    }

    [Fact]
    public void Another_system_message_passes_through()
    {
        var field = new Field { Value = Description };
        var sent = Join(new JoinDescriptionChatGuard(), field, descriptionInChat: false);

        Assert.Contains(OtherMessage, sent);
    }

    [Fact]
    public void An_empty_description_is_left_alone()
    {
        foreach (string? empty in new[] { "", null })
        {
            var field = new Field { Value = empty };
            var guard = new JoinDescriptionChatGuard();

            Assert.False(guard.Before(field, descriptionInChat: false));
            Assert.False(guard.Pending);
            Assert.Equal(new[] { OtherMessage }, GameJoin(field));
            Assert.False(guard.Restore(field));
            Assert.Equal(empty, field.Value);
            Assert.Equal(0, field.Writes);
        }
    }

    [Fact]
    public void A_whitespace_description_is_held_too_because_the_game_would_send_it()
    {
        var field = new Field { Value = " " };
        var sent = Join(new JoinDescriptionChatGuard(), field, descriptionInChat: false);

        Assert.Equal(new[] { OtherMessage }, sent);
        Assert.Equal(" ", field.Value);
    }

    [Fact]
    public void The_setting_on_restores_the_vanilla_chat_line()
    {
        var field = new Field { Value = Description };
        var guard = new JoinDescriptionChatGuard();

        var sent = Join(guard, field, descriptionInChat: true);

        Assert.Equal(new[] { OtherMessage, Description }, sent);
        Assert.Equal(0, field.Writes);
        Assert.False(guard.Pending);
    }

    [Fact]
    public void A_missed_restore_is_made_good_by_the_next_join_or_the_frame()
    {
        var field = new Field { Value = Description };
        var guard = new JoinDescriptionChatGuard();

        // The original threw: no postfix ran.
        Assert.True(guard.Before(field, descriptionInChat: false));
        Assert.Equal("", field.Value);
        Assert.True(guard.Pending);

        // The next join first puts it back, then holds it again for its own call.
        Assert.True(guard.Before(field, false));
        Assert.Equal(new[] { OtherMessage }, GameJoin(field));
        Assert.True(guard.Restore(field));
        Assert.Equal(Description, field.Value);

        // The frame safety net is the same Restore.
        guard.Before(field, false);
        Assert.True(guard.Restore(field));
        Assert.False(guard.Restore(field));
        Assert.Equal(Description, field.Value);
    }

    [Fact]
    public void A_description_set_during_the_join_is_kept()
    {
        var field = new Field { Value = Description };
        var guard = new JoinDescriptionChatGuard();

        guard.Before(field, false);
        GameJoin(field, midJoin: () => field.Value = "New text");
        Assert.False(guard.Restore(field));

        Assert.Equal("New text", field.Value);
        Assert.False(guard.Pending);
    }

    [Fact]
    public void The_setting_change_applies_on_the_next_join()
    {
        var field = new Field { Value = Description };
        var guard = new JoinDescriptionChatGuard();

        Assert.Equal(new[] { OtherMessage }, Join(guard, field, false));
        Assert.Equal(new[] { OtherMessage, Description }, Join(guard, field, true));
        Assert.Equal(new[] { OtherMessage }, Join(guard, field, false));
        Assert.Equal(Description, field.Value);
    }
}
