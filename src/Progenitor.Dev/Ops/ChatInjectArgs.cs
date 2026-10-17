using System;
using System.Globalization;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>A parsed <c>command</c> request: a line typed as a connected player.</summary>
internal sealed class ChatInjectRequest
{
    public string Text { get; init; } = "";

    /// <summary>The speaker's <c>User.Index</c>, when given.</summary>
    public int? UserIndex { get; init; }

    /// <summary>The speaker's character name, when given (and no index).</summary>
    public string? CharacterName { get; init; }

    /// <summary>The game's <c>ChatMessageType</c> value: Global 0, Team 2, Local 4.</summary>
    public int MessageType { get; init; } = ChatInjectArgs.Local;

    /// <summary>The channel's name as the reply reports it (<c>local</c>, <c>global</c>, <c>clan</c>).</summary>
    public string Channel { get; init; } = "local";

    /// <summary>Also show the line in the speaker's own chat, as their client does when they type it.</summary>
    public bool Echo { get; init; } = true;
}

/// <summary>
/// Game-free argument parsing for the debug-bridge <c>command</c> op, so the test project compiles it. Shape:
/// <c>{"text":"hello","user":"Name"|12,"userIndex":12,"channel":"local"|"global"|"clan","echo":true}</c>. Only
/// <c>text</c> is required. <c>userIndex</c> (a number or a numeric string) wins over <c>user</c>; <c>user</c> as a
/// number or numeric string is a <c>User.Index</c>, any other string a character name. Whisper and System are refused:
/// a whisper needs a receiver, and a System line is admin-only and already reaches its sender.
/// </summary>
internal static class ChatInjectArgs
{
    internal const int Global = 0;
    internal const int Team = 2;
    internal const int Local = 4;

    public static ChatInjectRequest Parse(JsonElement args)
    {
        string text = Str(args, "text") ?? "";
        if (text.Length == 0) throw new ArgumentException("args.text is required");

        ResolveUser(args, out var userIndex, out var characterName);

        string channel = (Str(args, "channel") ?? "local").Trim().ToLowerInvariant();
        int type = channel switch
        {
            "" or "local" => Local,
            "global" => Global,
            "clan" or "team" => Team,
            _ => throw new ArgumentException("args.channel must be local, global or clan"),
        };
        if (channel.Length == 0) channel = "local";
        if (channel == "team") channel = "clan";

        bool echo = true;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("echo", out var e))
        {
            if (e.ValueKind == JsonValueKind.False) echo = false;
            else if (e.ValueKind != JsonValueKind.True) throw new ArgumentException("args.echo must be true or false");
        }

        return new ChatInjectRequest
        {
            Text = text,
            UserIndex = userIndex,
            CharacterName = characterName,
            MessageType = type,
            Channel = channel,
            Echo = echo,
        };
    }

    /// <summary>
    /// The speaker: <c>userIndex</c> (int) wins; otherwise <c>user</c> as an int is <c>User.Index</c> (ghosts start at
    /// 10001) and any other string is the character name (e.g. <c>Ghost1</c>). Neither: the first connected user.
    /// </summary>
    internal static void ResolveUser(JsonElement args, out int? userIndex, out string? characterName)
    {
        userIndex = null;
        characterName = null;
        if (args.ValueKind != JsonValueKind.Object) return;

        if (args.TryGetProperty("userIndex", out var idxEl))
        {
            if (idxEl.ValueKind == JsonValueKind.Number && idxEl.TryGetInt32(out var n))
                userIndex = n;
            else if (idxEl.ValueKind == JsonValueKind.String
                     && int.TryParse(idxEl.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                userIndex = n;
        }

        if (!args.TryGetProperty("user", out var whoEl)) return;
        if (whoEl.ValueKind == JsonValueKind.Number && whoEl.TryGetInt32(out var asIndex))
        {
            userIndex ??= asIndex;
            return;
        }
        if (whoEl.ValueKind != JsonValueKind.String) return;
        var who = whoEl.GetString();
        if (string.IsNullOrWhiteSpace(who)) return;
        if (int.TryParse(who, NumberStyles.Integer, CultureInfo.InvariantCulture, out asIndex))
        {
            userIndex ??= asIndex;
            return;
        }
        if (userIndex == null) characterName = who;
    }

    private static string? Str(JsonElement args, string name) =>
        args.ValueKind == JsonValueKind.Object && args.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
