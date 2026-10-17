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
    /// <see cref="ChatMessageType"/> name (default System); <c>count</c> optional (1 to 100, default 1): that many
    /// lines, <c>text (i/count)</c>, sent in this one call, so they reach the client in the same frame (the burst a mod's
    /// event handler sends). Server, main thread.
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

        int count = 1;
        if (args.ValueKind == JsonValueKind.Object && args.TryGetProperty("count", out var c))
        {
            if (c.ValueKind != JsonValueKind.Number || !c.TryGetInt32(out count) || count < 1 || count > 100)
                throw new ArgumentException("args.count must be a whole number from 1 to 100");
        }

        int recipients = 0;
        for (int i = 1; i <= count; i++)
        {
            int n = Progenitor.Chat.Chat.Broadcast(count == 1 ? text : text + " (" + i + "/" + count + ")", type);
            if (i == 1) recipients = n;
        }

        return new
        {
            recipients,
            count,
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
