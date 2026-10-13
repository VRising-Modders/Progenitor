using System;

namespace Progenitor.Networking;

/// <summary>Game-free request and response checks shared by the server and client channel.</summary>
internal static class ItemManagementPolicy
{
    internal const int MaxReasonChars = 512;
    internal static bool ValidRequest(int version, bool response, int nonce, string? owner, string? decision) =>
        version == 1 && !response && nonce > 0 && owner != null && owner.Length <= 256
        && decision != null && decision.Length <= 16;

    internal static string Refusal(bool handshakeOk, bool admin, string owner, string decision)
    {
        if (!handshakeOk) return "Complete the Progenitor handshake first.";
        if (!admin) return "Only an admin of this server may manage saved items.";
        if (decision.Length == 0) return owner.Length == 0 ? "" : "A status request cannot name an owner.";
        if (decision != "restore" && decision != "retry" && decision != "keep" && decision != "discard")
            return "Unknown item decision.";
        return string.IsNullOrWhiteSpace(owner) ? "An item decision must name its mod owner." : "";
    }

    internal static bool MatchesResponse(bool response, int nonce, int pending) => response && pending > 0 && nonce == pending;

    internal static bool MatchesResponse(bool response, int nonce, int pending, string? epoch, string expectedEpoch) =>
        MatchesResponse(response, nonce, pending) && string.Equals(epoch, expectedEpoch, StringComparison.Ordinal);

    internal static string Clip(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text)) return "";
        if (text.Length <= maxChars) return text;
        int length = maxChars;
        if (length > 0 && char.IsHighSurrogate(text[length - 1])) length--;
        return text.Substring(0, length);
    }
}
