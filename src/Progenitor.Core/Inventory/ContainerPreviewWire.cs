using System;
using System.Text.Json;

namespace Progenitor.Inventory;

/// <summary>
/// The container preview channel's packets, game-free. One Core-reserved event id (<c>'PrCp'</c>) carries both
/// directions; <see cref="ContainerPreviewPacket.Kind"/> keeps an answer from ever decoding as a request, and each side
/// registers only its own direction. Client → server: <c>open</c>, <c>renew</c>, <c>close</c>. Server → client:
/// <c>reply</c> (to an open), <c>hold</c> (keep waiting: the open gate asks the player first) and <c>ended</c> (the server
/// let go: the lease ended, access changed, the container is gone).
/// The network id words in a reply are session-local; never persist them.
/// </summary>
internal static class ContainerPreviewWire
{
    /// <summary><c>'PrCp'</c>, inside Core's reserved range.</summary>
    internal const int EventId = 0x50724370;

    internal const int Version = 1;
    internal const int MaxBytes = 2048;
    internal const int MaxText = 300;

    internal const string KindOpen = "open";
    internal const string KindRenew = "renew";
    internal const string KindClose = "close";
    internal const string KindReply = "reply";
    internal const string KindEnded = "ended";

    /// <summary>
    /// Server to client: keep waiting for the reply (the container open gate is asking the player first); <c>Seconds</c>
    /// is how much longer. An older client drops the kind and closes with NoAnswer after its usual wait.
    /// </summary>
    internal const string KindHold = "hold";

    internal static bool IsRequestKind(string? kind) => kind is KindOpen or KindRenew or KindClose;

    internal static bool IsAnswerKind(string? kind) => kind is KindReply or KindEnded or KindHold;

    internal static byte[] Encode(ContainerPreviewPacket packet) => JsonSerializer.SerializeToUtf8Bytes(packet);

    /// <summary>The packet, or null when it is not a well-formed packet of this version for the expected direction.</summary>
    internal static ContainerPreviewPacket? Decode(ReadOnlySpan<byte> bytes, bool expectRequest)
    {
        if (bytes.Length <= 0 || bytes.Length > MaxBytes) return null;
        ContainerPreviewPacket? p;
        try { p = JsonSerializer.Deserialize<ContainerPreviewPacket>(bytes); }
        catch { return null; }
        if (p == null || p.Version != Version || p.Sequence <= 0) return null;
        if (expectRequest ? !IsRequestKind(p.Kind) : !IsAnswerKind(p.Kind)) return null;
        if (p.Container == null || p.Container.Length > 64 || p.Message == null || p.Message.Length > MaxText) return null;
        if (expectRequest && !double.IsFinite(p.Seconds)) return null;
        return p;
    }

    /// <summary>Trim a sentence to the wire's limit.</summary>
    internal static string Clip(string? text) =>
        string.IsNullOrEmpty(text) ? "" : text!.Length <= MaxText ? text : text.Substring(0, MaxText);
}

/// <summary>One container preview packet (<see cref="ContainerPreviewWire"/>).</summary>
internal sealed class ContainerPreviewPacket
{
    public int Version { get; set; } = ContainerPreviewWire.Version;

    /// <summary><c>open</c>, <c>renew</c>, <c>close</c>, <c>reply</c> or <c>ended</c>.</summary>
    public string Kind { get; set; } = "";

    /// <summary>The client's preview number; every answer names the one it is about, so a late answer never reaches a newer preview.</summary>
    public int Sequence { get; set; }

    /// <summary>The <see cref="ContainerId"/> text (<c>c-</c> plus 32 hex digits).</summary>
    public string Container { get; set; } = "";

    /// <summary>open / renew: the lease seconds asked for; hold: how much longer the client waits for the reply.</summary>
    public double Seconds { get; set; }

    /// <summary>
    /// open: put the user in the chest's inventory sync lists for the lease (default true; a packet without the field,
    /// from an older client, gets true). False only for the Dev bridge's E1b comparison.
    /// </summary>
    public bool SyncInventory { get; set; } = true;

    /// <summary>STATIONPREVIEW1, open: the client can open station windows (an older client's packet has no field: false).</summary>
    public bool Stations { get; set; }

    /// <summary>STATIONPREVIEW1, reply: the window to open (<c>HUDMenuType</c>); 0 from an older server means the container window.</summary>
    public int MenuType { get; set; }

    /// <summary>reply: the server agreed.</summary>
    public bool Ok { get; set; }

    /// <summary>reply / ended: the player-facing sentence.</summary>
    public string Message { get; set; } = "";

    /// <summary>reply: the container's <c>NetworkId</c> words.</summary>
    public int NetIndex { get; set; }
    public int NetGeneration { get; set; }
    public int NetType { get; set; }
}
