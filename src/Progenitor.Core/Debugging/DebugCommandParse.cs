using System;
using System.Globalization;

namespace Progenitor.Debugging;

/// <summary>What one <c>.debug</c> command asks for.</summary>
internal enum DebugAction
{
    /// <summary>Read back what is actually applied. A bare <c>.debug</c> is this.</summary>
    Status = 0,

    /// <summary>Apply the debug buff (or re-arm its guard when it is already on).</summary>
    On = 1,

    /// <summary>Remove the debug buff and what it granted.</summary>
    Off = 2,

    /// <summary>Teleport the caller to a world X/Z (the map teleport's server path).</summary>
    Teleport = 3,

    /// <summary>Read back the server-wide build mode. <c>.debug build</c> or <c>.debug build status</c>.</summary>
    BuildStatus = 4,

    /// <summary>Turn the server-wide build mode on (<c>.debug build on</c>).</summary>
    BuildOn = 5,

    /// <summary>Turn the server-wide build mode off, restoring what it changed (<c>.debug build off</c>).</summary>
    BuildOff = 6,

    /// <summary>List pieces that belong to no castle (<c>.debug build detached</c>).</summary>
    BuildDetached = 7,

    /// <summary>Remove the caller's own detached pieces (<c>.debug build detached remove</c>).</summary>
    BuildDetachedRemove = 8,

    /// <summary>Remove every player's detached pieces (<c>.debug build detached remove all</c>).</summary>
    BuildDetachedRemoveAll = 9,
}

/// <summary>
/// A parsed <c>.debug</c> command. <see cref="Player"/> is empty for the caller's own character;
/// <see cref="X"/> and <see cref="Z"/> are set only for <see cref="DebugAction.Teleport"/>.
/// </summary>
internal readonly record struct DebugRequest(DebugAction Action, string Player, float X, float Z, string Piece = "", int Confirm = -1)
{
    /// <summary>True when the command names another player rather than the caller.</summary>
    internal bool TargetsOther => Player.Length > 0;

    /// <summary>True for the server-wide build mode forms (<c>.debug build ...</c>).</summary>
    internal bool IsBuild => Action is DebugAction.BuildStatus or DebugAction.BuildOn or DebugAction.BuildOff || IsDetached;

    /// <summary>True for the detached-piece forms (<c>.debug build detached ...</c>).</summary>
    internal bool IsDetached => Action is DebugAction.BuildDetached or DebugAction.BuildDetachedRemove or DebugAction.BuildDetachedRemoveAll;
}

/// <summary>
/// Game-free parsing for Core's <c>.debug</c> command (<see cref="DebugCommands"/>):
/// <list type="bullet">
/// <item><c>.debug</c> or <c>.debug status [player]</c> — the readout;</item>
/// <item><c>.debug on [player]</c> / <c>.debug off [player]</c> — another online player when named;</item>
/// <item><c>.debug tp &lt;x&gt; &lt;z&gt;</c> — the map teleport's server path for the caller;</item>
/// <item><c>.debug build [on|off|status]</c> — the server-wide sandbox build mode;</item>
/// <item><c>.debug build detached [remove [&lt;count&gt;|&lt;id&gt;|all &lt;count&gt;]]</c> — pieces that belong to no castle.</item>
/// </list>
/// Nothing here checks who is asking. The root <c>.debug</c> and the separate <c>.debug build</c>
/// subcommand (<c>DebugBuildCommands</c>) both check the server's admin-list rule before this runs.
/// </summary>
internal static class DebugCommandParse
{
    /// <summary>The usage line <c>.help debug</c> and every parse error show.</summary>
    internal const string Usage = ".debug [on|off|status] [player] | .debug tp <x> <z> | " + BuildUsage;

    /// <summary>The <c>.debug build</c> forms.</summary>
    internal const string BuildUsage = ".debug build [on|off|status] | .debug build detached [remove [<count>|<id>|all <count>]]";

    /// <summary>The reply for an argument this parser does not know.</summary>
    internal const string UsageReply = "Usage: " + Usage;

    /// <summary>The reply for <c>.debug tp</c> without two numbers.</summary>
    internal const string TeleportUsageReply = "Usage: .debug tp <x> <z> (world coordinates, for example .debug tp -1200 850)";

    /// <summary>The reply for <c>.debug build</c> with an argument it does not know.</summary>
    internal const string BuildUsageReply = "Usage: " + BuildUsage + " (server-wide sandbox build mode; detached lists or removes pieces that belong to no castle)";

    /// <summary>
    /// Parse the first argument (<paramref name="what"/>) and the rest of the line
    /// (<paramref name="rest"/>). False with <paramref name="error"/> set to the reply when the
    /// input is not a <c>.debug</c> form; nothing is run then.
    /// </summary>
    internal static bool TryParse(string? what, string? rest, out DebugRequest request, out string error)
    {
        request = new DebugRequest(DebugAction.Status, "", 0f, 0f);
        error = "";
        string verb = (what ?? "").Trim().ToLowerInvariant();
        string tail = (rest ?? "").Trim();

        switch (verb)
        {
            case "":
                if (tail.Length > 0)
                {
                    error = UsageReply;
                    return false;
                }
                return true;
            case "status":
                request = new DebugRequest(DebugAction.Status, PlayerName(tail), 0f, 0f);
                return true;
            case "on":
                request = new DebugRequest(DebugAction.On, PlayerName(tail), 0f, 0f);
                return true;
            case "off":
                request = new DebugRequest(DebugAction.Off, PlayerName(tail), 0f, 0f);
                return true;
            case "tp":
                if (!TryParsePoint(tail, out float x, out float z))
                {
                    error = TeleportUsageReply;
                    return false;
                }
                request = new DebugRequest(DebugAction.Teleport, "", x, z);
                return true;
            case "build":
                var words = tail.ToLowerInvariant().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (words.Length >= 1 && words[0] == "detached")
                {
                    if (TryParseDetached(words, out request)) return true;
                    error = BuildUsageReply;
                    return false;
                }
                switch (string.Join(" ", words))
                {
                    case "":
                    case "status":
                        request = new DebugRequest(DebugAction.BuildStatus, "", 0f, 0f);
                        return true;
                    case "on":
                        request = new DebugRequest(DebugAction.BuildOn, "", 0f, 0f);
                        return true;
                    case "off":
                        request = new DebugRequest(DebugAction.BuildOff, "", 0f, 0f);
                        return true;
                    default:
                        error = BuildUsageReply;
                        return false;
                }
            default:
                error = UsageReply;
                return false;
        }
    }

    /// <summary>
    /// <c>detached</c>, <c>detached remove [&lt;count&gt;]</c>, <c>detached remove all [&lt;count&gt;]</c>
    /// and <c>detached remove &lt;index:version&gt;</c>. The count confirms how many pieces the
    /// remove takes (<see cref="DetachedPieces.ConfirmRefusal"/>); -1 when not given.
    /// </summary>
    private static bool TryParseDetached(string[] words, out DebugRequest request)
    {
        request = default;
        if (words.Length == 1)
        {
            request = new DebugRequest(DebugAction.BuildDetached, "", 0f, 0f);
            return true;
        }
        if (words[1] != "remove" || words.Length > 4) return false;
        if (words.Length == 2)
        {
            request = new DebugRequest(DebugAction.BuildDetachedRemove, "", 0f, 0f);
            return true;
        }
        if (words[2] == "all")
        {
            int confirm = -1;
            if (words.Length == 4 && !TryCount(words[3], out confirm)) return false;
            request = new DebugRequest(DebugAction.BuildDetachedRemoveAll, "", 0f, 0f, "", confirm);
            return true;
        }
        if (words.Length != 3) return false;
        if (IsEntityId(words[2]))
        {
            request = new DebugRequest(DebugAction.BuildDetachedRemove, "", 0f, 0f, words[2]);
            return true;
        }
        if (!TryCount(words[2], out int own)) return false;
        request = new DebugRequest(DebugAction.BuildDetachedRemove, "", 0f, 0f, "", own);
        return true;
    }

    private static bool TryCount(string text, out int count) =>
        int.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out count) && count > 0;

    /// <summary>An entity id as the detached list prints it: <c>index:version</c>, both non-negative integers.</summary>
    internal static bool IsEntityId(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        int colon = text.IndexOf(':');
        if (colon <= 0 || colon == text.Length - 1) return false;
        return int.TryParse(text.Substring(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out _)
            && int.TryParse(text.Substring(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out _);
    }

    /// <summary>
    /// The player name from the rest of the line: trimmed, and one pair of matching surrounding
    /// quotes (<c>"Name"</c> or <c>'Name'</c>) removed, because the [Remainder] argument keeps
    /// them verbatim and a quoted name would otherwise never match.
    /// </summary>
    internal static string PlayerName(string? text)
    {
        string name = (text ?? "").Trim();
        if (name.Length >= 2)
        {
            char first = name[0], last = name[name.Length - 1];
            if ((first == '"' || first == '\x27') && last == first)
                name = name.Substring(1, name.Length - 2).Trim();
        }
        return name;
    }

    /// <summary>Exactly two finite invariant-culture numbers separated by whitespace.</summary>
    internal static bool TryParsePoint(string? text, out float x, out float z)
    {
        x = 0f;
        z = 0f;
        var parts = (text ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length != 2) return false;
        if (!float.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out x)) return false;
        if (!float.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out z)) return false;
        return !float.IsNaN(x) && !float.IsInfinity(x) && !float.IsNaN(z) && !float.IsInfinity(z);
    }

    /// <summary>The <see cref="DebugBuff.Kind"/> an on/off/status request runs.</summary>
    internal static DebugBuff.Kind KindOf(DebugAction action) => action switch
    {
        DebugAction.On => DebugBuff.Kind.On,
        DebugAction.Off => DebugBuff.Kind.Off,
        _ => DebugBuff.Kind.Status,
    };

    /// <summary>The line the admin sees above another player's readout.</summary>
    internal static string FormatTargetHeader(string name, DebugAction action) =>
        "Debug " + ActionWord(action) + " for " + name + ":";

    /// <summary>What the other player is told after an admin changed their debug buff.</summary>
    internal static string FormatTargetNotice(DebugAction action) => action switch
    {
        DebugAction.On => "An admin turned the debug buff on for you.",
        DebugAction.Off => "An admin turned the debug buff off for you.",
        _ => "",
    };

    /// <summary>The reply when no online player has that name.</summary>
    internal static string FormatNoSuchPlayer(string name) =>
        "No online player named '" + name + "'. Names are whole and case-insensitive.";

    /// <summary>The reply when the name matches more than one online player.</summary>
    internal static string FormatAmbiguousPlayer(string name, int count) =>
        "'" + name + "' matches " + count + " online players; use the exact name.";

    private static string ActionWord(DebugAction action) => action switch
    {
        DebugAction.On => "on",
        DebugAction.Off => "off",
        DebugAction.Teleport => "tp",
        DebugAction.BuildOn => "build on",
        DebugAction.BuildOff => "build off",
        DebugAction.BuildStatus => "build status",
        _ => "status",
    };
}
