using System;
using System.Text.Json;

namespace Progenitor.Inventory;

/// <summary>
/// The container open gate's packets, game-free. One Core-reserved event id (<c>'PrCg'</c>) carries both directions;
/// <see cref="ContainerGatePacket.Kind"/> keeps a server packet from ever decoding as a client one. Server to client:
/// <c>prompt</c> (show a masked prompt), <c>denied</c> (an open was refused; show a toast), <c>result</c> (what an
/// answer did). Client to server: <c>answer</c> (the typed text) and <c>cancel</c>.
///
/// <para>The answer's text is in the packet and nowhere else: neither side logs a packet's contents, and
/// <see cref="ContainerGatePacket.ToString"/> leaves the text out.</para>
/// </summary>
internal static class ContainerGateWire
{
    /// <summary><c>'PrCg'</c>, inside Core's reserved range.</summary>
    internal const int EventId = 0x50724367;

    internal const int Version = 1;
    internal const int MaxBytes = 2048;

    internal const string KindPrompt = "prompt";
    internal const string KindDenied = "denied";
    internal const string KindResult = "result";
    internal const string KindAnswer = "answer";
    internal const string KindCancel = "cancel";

    internal static bool IsClientKind(string? kind) => kind is KindAnswer or KindCancel;

    internal static bool IsServerKind(string? kind) => kind is KindPrompt or KindDenied or KindResult;

    internal static byte[] Encode(ContainerGatePacket packet) => JsonSerializer.SerializeToUtf8Bytes(packet);

    /// <summary>The packet, or null when it is not a well-formed packet of this version for the expected direction.</summary>
    internal static ContainerGatePacket? Decode(ReadOnlySpan<byte> bytes, bool fromClient)
    {
        if (bytes.Length <= 0 || bytes.Length > MaxBytes) return null;
        ContainerGatePacket? p;
        try { p = JsonSerializer.Deserialize<ContainerGatePacket>(bytes); }
        catch { return null; }
        if (p == null || p.Version != Version) return null;
        if (fromClient ? !IsClientKind(p.Kind) : !IsServerKind(p.Kind)) return null;
        p.Container ??= "";
        p.Title ??= "";
        p.Body ??= "";
        p.Message ??= "";
        p.Mod ??= "";
        p.Text ??= "";
        if (p.Container.Length > 64 || p.Title.Length > ContainerOpenDecision.MaxText || p.Body.Length > ContainerOpenDecision.MaxText
            || p.Message.Length > ContainerOpenDecision.MaxText || p.Mod.Length > 128) return null;
        if (p.Text.Length > ContainerOpenDecision.MaxAnswerLength) return null;
        // Only an answer carries text; anything else with text is malformed.
        if (p.Kind != KindAnswer && p.Text.Length > 0) return null;
        if (p.Kind is KindPrompt or KindAnswer or KindCancel or KindResult && p.Sequence <= 0) return null;
        if (p.Kind == KindPrompt && (p.MaxLength < 1 || p.MaxLength > ContainerOpenDecision.MaxAnswerLength)) return null;
        return p;
    }
}

/// <summary>One container open gate packet (<see cref="ContainerGateWire"/>).</summary>
internal sealed class ContainerGatePacket
{
    public int Version { get; set; } = ContainerGateWire.Version;

    /// <summary><c>prompt</c>, <c>denied</c>, <c>result</c>, <c>answer</c> or <c>cancel</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The server's prompt number; an answer, a cancel and a result name the prompt they are about.</summary>
    public int Sequence { get; set; }

    /// <summary>The <see cref="ContainerId"/> text.</summary>
    public string Container { get; set; } = "";

    /// <summary>How the open was asked for (<see cref="ContainerOpenKind"/> as a number).</summary>
    public int OpenKind { get; set; }

    /// <summary>prompt: the title and the line under it.</summary>
    public string Title { get; set; } = "";

    public string Body { get; set; } = "";

    /// <summary>prompt: the field's character limit.</summary>
    public int MaxLength { get; set; }

    /// <summary>prompt / denied / result: the plugin that asked or refused (its name prefixes the toast).</summary>
    public string Mod { get; set; } = "";

    /// <summary>denied / result: the player-facing sentence.</summary>
    public string Message { get; set; } = "";

    /// <summary>result: the answer was right and the open goes on.</summary>
    public bool Ok { get; set; }

    /// <summary>result: the interaction was closed and the client should repeat it now.</summary>
    public bool Retry { get; set; }

    /// <summary>result with Retry: the container's <c>NetworkId</c> words (session-local).</summary>
    public int NetIndex { get; set; }
    public int NetGeneration { get; set; }

    /// <summary>prompt: show the Remember password box; answer: the player ticked it.</summary>
    public bool Remember { get; set; }

    /// <summary>answer: the typed text. Never logged.</summary>
    public string Text { get; set; } = "";

    /// <summary>Everything but the typed text.</summary>
    public override string ToString() =>
        Kind + " #" + Sequence + " " + Container + (Mod.Length > 0 ? " by " + Mod : "") + (Kind == ContainerGateWire.KindAnswer ? " (" + Text.Length + " characters)" : "")
        + (Kind == ContainerGateWire.KindResult ? (Ok ? " ok" : " refused") + (Retry ? " retry" : "") : "");
}
