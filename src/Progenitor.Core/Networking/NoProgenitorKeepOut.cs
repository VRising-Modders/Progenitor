using System;

namespace Progenitor.Networking;

/// <summary>
/// KEEPOUT1 (decided 2026-10-06): the server's explicit choice for players whose game never answers the handshake
/// (no Progenitor): <b>Let them play</b> (default, today's behavior) or <b>Keep them out</b>. Game-free so the decision
/// has a matrix test; <c>Handshake</c> applies it where it arms the answer deadline.
///
/// <list type="bullet">
/// <item>Whether a silent player is disconnected: Keep them out, <b>or</b> a server plugin needs Progenitor on clients
/// (one that is not NoNeedForSync or ClientOnly), <b>or</b> the client mod policy needs an answer (a required client
/// mod, or Only approved mods with Refuse them). Let them play keeps only the last two.</item>
/// <item>After how long: <c>[Networking] HandshakeAnswerTimeoutSeconds</c>, after the Message without Progenitor. With
/// Let them play, 0 still means no deadline (as before). With Keep them out, 0 cannot mean "let them in", so it is
/// raised to <see cref="KeepOutMinSeconds"/>: the message still reaches them before the disconnect.</item>
/// </list>
/// </summary>
internal static class NoProgenitorKeepOut
{
    /// <summary>The cfg key (<c>[Networking]</c> of the server's <c>progenitor.cfg</c>).</summary>
    internal const string ConfigKey = "RequireProgenitor";

    /// <summary>The default: let them play (servers that never chose do not change).</summary>
    internal const bool Default = false;

    /// <summary>Keep them out with a 0 s deadline: they are disconnected after this many seconds, the message first.</summary>
    internal const int KeepOutMinSeconds = 10;

    internal const string ValueLetPlay = "false";
    internal const string ValueKeepOut = "true";

    /// <summary>True when a player whose game never answers is disconnected.</summary>
    internal static bool Disconnects(bool keepOut, bool serverPluginNeedsProgenitor, bool policyNeedsAnswer) =>
        keepOut || serverPluginNeedsProgenitor || policyNeedsAnswer;

    /// <summary>The deadline the server arms, in seconds; 0 arms none (only possible with Let them play).</summary>
    internal static int Timeout(bool keepOut, int configuredSeconds)
    {
        int s = Math.Max(0, configuredSeconds);
        return keepOut && s == 0 ? KeepOutMinSeconds : s;
    }

    /// <summary>The kick's list when only this choice asks for Progenitor (no server plugin names a mod).</summary>
    internal const string KeepOutSummary = "Progenitor";
}
