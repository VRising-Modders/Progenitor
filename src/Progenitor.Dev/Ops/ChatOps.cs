using System;
using System.Text.Json;
using ProjectM.Network;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Debug-bridge <c>broadcast</c> op: send a chat line to every <c>ConnectedUser</c> through
/// <see cref="Progenitor.Chat.Chat.Broadcast"/>, which skips the vanilla job (that job never echoes to the
/// speaker). Returns the recipient count so a one-player live check is
/// <c>recipients: 1</c> plus <c>chat_observe</c> <c>chatServerEvents: 1</c>.
/// </summary>
internal static class ChatOps
{
    /// <summary>
    /// <paramref name="args"/>: <c>text</c> required; <c>type</c> optional inbound
    /// <see cref="ChatMessageType"/> name (default System). Server, main thread.
    /// </summary>
    public static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("broadcast runs on the server side only");

        var text = GetString(args, "text");
        if (string.IsNullOrEmpty(text))
            throw new ArgumentException("args.text is required");

        var type = ChatMessageType.System;
        var typeName = GetString(args, "type");
        if (!string.IsNullOrWhiteSpace(typeName)
            && !Enum.TryParse(typeName, ignoreCase: true, out type))
            throw new ArgumentException("args.type must be a ChatMessageType name (Global, Region, Team, Whisper, Local, System)");

        int recipients = Progenitor.Chat.Chat.Broadcast(text, type);
        return new
        {
            recipients,
            type = type.ToString(),
            text,
        };
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el)) return null;
        return el.ValueKind == JsonValueKind.String ? el.GetString() : null;
    }
}
