// SPDX-License-Identifier: MIT
using System;
using Progenitor.Networking;

namespace ProgenitorMod;

// Game-free: only System and Progenitor.Networking, so the tests link this file and run without the game.
// Wire rule: a new field is a minor version, a removed or retyped one a major version. Never reuse a field number.

/// <summary>Client to server: ask for the server's greeting. Version 1, written by 1.0.0.</summary>
[ModMessage("greeting.get")]
internal sealed class GreetingRequest
{
    [Field(1)] public string ClientVersion = "";
}

/// <summary>Server to client: the greeting and the server part's version. Version 1, written by 1.0.0.</summary>
[ModMessage("greeting")]
internal sealed class GreetingReply
{
    [Field(1)] public string Text = "";

    [Field(2)] public string ServerVersion = "";
}

internal static class ModRules
{
    internal const int MaxGreetingLength = 120;

    internal const string DefaultGreeting = "Welcome to the server!";

    internal const int MaxRetries = 3;

    /// <summary>The server's answer. Throw <see cref="RequestRefusedException"/> to refuse with a reason.</summary>
    internal static GreetingReply Answer(GreetingRequest request, string greeting, string serverVersion)
    {
        if (request == null) throw new RequestRefusedException("empty request");
        string text = (greeting ?? "").Trim();
        if (text.Length == 0) throw new RequestRefusedException("the server has no greeting set");
        if (text.Length > MaxGreetingLength) text = text.Substring(0, MaxGreetingLength);
        return new GreetingReply { Text = text, ServerVersion = serverVersion ?? "" };
    }

    /// <summary>Failures that usually clear by themselves (just after a join or reload, a busy server): retry quietly.</summary>
    internal static bool ShouldRetry(RequestStatus status) =>
        status is RequestStatus.Timeout or RequestStatus.Busy or RequestStatus.NotNegotiated
            or RequestStatus.RateLimited or RequestStatus.Unloaded;

    internal static double RetryDelaySeconds(int attempt) => Math.Min(8, 2 * Math.Pow(2, Math.Max(0, attempt - 1)));

    /// <summary>One plain sentence for the status line. Never exception text.</summary>
    internal static string Describe(RequestStatus status, string reason) => status switch
    {
        RequestStatus.Ok => "The server answered.",
        RequestStatus.Refused => "The server said no: " + (string.IsNullOrWhiteSpace(reason) ? "no reason given" : reason.Trim()) + ".",
        RequestStatus.NotSent or RequestStatus.NoHandler => "This server does not run the server part of this mod.",
        RequestStatus.Disconnected => "Join a server to ask it.",
        RequestStatus.Cancelled => "Cancelled.",
        RequestStatus.Unsupported => "This server runs a version of the mod that cannot answer this one.",
        _ => "The server did not answer. Open the window again to retry.",
    };
}
