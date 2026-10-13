using Progenitor.Networking;

namespace Progenitor.SampleClientLed;

// The client-led sample's wire contract: its two versioned messages (ctx.Messages) and the feature the server part
// offers (ctx.Features). Game-free on purpose, with no using but Progenitor.Networking: test projects link this file as
// source (tests/Progenitor.Testing.Tests replays recorded fixtures through it). Under the compatibility contract a
// new field or feature is a minor version, a removed or retyped one a major version.

/// <summary>The message and feature names of the client-led sample.</summary>
internal static class ClientLedMessages
{
    /// <summary>Message name of <see cref="TimeRequest"/>.</summary>
    internal const string Request = "time.request";

    /// <summary>Message name of <see cref="TimeAnswer"/>.</summary>
    internal const string Answer = "time.answer";

    /// <summary>Offered by the server part: it answers <see cref="TimeRequest"/> with <see cref="TimeAnswer"/>.</summary>
    internal const string FeatureTime = "time";
}

/// <summary>Client to server: "what time is it on your world". Version 1, written by 1.0.0.</summary>
[ModMessage(ClientLedMessages.Request)]
internal sealed class TimeRequest
{
    /// <summary>The client's request counter; the answer repeats it.</summary>
    [Field(1)] public int Sequence;
}

/// <summary>Server to client: one read of the world clock and the server's uptime. Version 1, written by 1.0.0.</summary>
[ModMessage(ClientLedMessages.Answer)]
internal sealed class TimeAnswer
{
    /// <summary>The request's <see cref="TimeRequest.Sequence"/>.</summary>
    [Field(1)] public int Sequence;

    /// <summary>The world's day number.</summary>
    [Field(2)] public long Day;

    /// <summary>The hour of the day, 0 to 24 (23.5 is 23:30).</summary>
    [Field(3)] public double Hour;

    /// <summary>Real seconds per game day.</summary>
    [Field(4)] public double DayLengthSeconds;

    /// <summary>The hour the sun rises.</summary>
    [Field(5)] public double SunriseHour;

    /// <summary>The hour the sun sets.</summary>
    [Field(6)] public double SunsetHour;

    /// <summary>Minutes since the server process started.</summary>
    [Field(7)] public int UptimeMinutes;
}
