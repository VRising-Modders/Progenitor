using System;
using System.Collections.Generic;
using Progenitor.Castles;

namespace Progenitor.Inventory;

/// <summary>Where a <see cref="IContainerPreview"/> is (<c>ctx.Inventory.Preview.Open</c>).</summary>
public enum ContainerPreviewState
{
    /// <summary>Asked the server; no answer yet.</summary>
    Requesting = 0,
    /// <summary>The server agreed; waiting for the container and its inventory to reach this client, or for the window to open.</summary>
    Waiting = 1,
    /// <summary>The game's container window shows the container.</summary>
    Open = 2,
    /// <summary>Ended; <see cref="IContainerPreview.CloseReason"/> says why.</summary>
    Closed = 3,
}

/// <summary>Why a container preview closed (<see cref="IContainerPreview.CloseReason"/>).</summary>
public enum ContainerPreviewCloseReason
{
    /// <summary>Still running.</summary>
    None = 0,
    /// <summary>The caller disposed it.</summary>
    Disposed = 1,
    /// <summary>The player pressed Escape while the preview was the topmost thing on screen.</summary>
    Escape = 2,
    /// <summary>The game closed the window or opened another menu over it (its own Escape, a real interaction).</summary>
    WindowClosed = 3,
    /// <summary>The server refused: not a plain container, not the user's or their clan's castle, unknown id. <see cref="IContainerPreview.Reason"/> says which.</summary>
    Refused = 4,
    /// <summary>The server did not answer (an older server, or no Progenitor on it).</summary>
    NoAnswer = 5,
    /// <summary>The container or its inventory did not reach this client within <see cref="ContainerPreviewOptions.ReplicationTimeoutSeconds"/>.</summary>
    ReplicationTimeout = 6,
    /// <summary>The window was asked to open and did not, or another game menu was open.</summary>
    OpenFailed = 7,
    /// <summary>A preview without <see cref="ContainerPreviewOptions.KeepOpen"/> reached its <see cref="ContainerPreviewOptions.Seconds"/>.</summary>
    Timeout = 8,
    /// <summary>The server ended its side (the player moved away, access changed, the container is gone, another view took the streaming origin).</summary>
    LeaseEnded = 9,
    /// <summary>The container's entity left this client while the window was open.</summary>
    TargetGone = 10,
    /// <summary>The client world ended or changed (disconnect, leaving, a new world).</summary>
    WorldChanged = 11,
    /// <summary>A newer preview replaced it (the window shows one container).</summary>
    Replaced = 12,
    /// <summary>The plugin (or Core) unloaded. <see cref="IContainerPreview.Closed"/> is not raised for this reason.</summary>
    Unloaded = 13,
    /// <summary>A game read or write failed; the preview ended rather than leave a half-open window.</summary>
    Failed = 14,
}

/// <summary>Options for <c>ctx.Inventory.Preview.Open</c>. The defaults are a 30 second preview with the bag hidden.</summary>
public sealed record ContainerPreviewOptions
{
    /// <summary>Most a timed preview may last, in seconds (ten minutes, the server lease's cap).</summary>
    public const double MaxSeconds = 600;

    /// <summary>Most <see cref="ReplicationTimeoutSeconds"/> may be.</summary>
    public const float MaxReplicationTimeoutSeconds = 30;

    /// <summary>
    /// Hide the player's own inventory panel (the bag) next to the container, so a mod window can sit there. Off by
    /// default: the window shows as the game shows it at a chest, the character inventory on the left and the chest on
    /// the right (the character inventory is the player's own; nothing moves into or out of the chest either way).
    /// </summary>
    public bool HidePlayerInventory { get; init; } = false;

    /// <summary>Keep the preview open until the player or the caller closes it; the client renews the server lease.</summary>
    public bool KeepOpen { get; init; }

    /// <summary>A timed preview's length, more than 0 and at most <see cref="MaxSeconds"/>. Ignored with <see cref="KeepOpen"/>.</summary>
    public double Seconds { get; init; } = 30;

    /// <summary>How long to wait for the container and its inventory to reach this client, more than 0 and at most 30.</summary>
    public float ReplicationTimeoutSeconds { get; init; } = 5;

    /// <summary>
    /// Drop this client's own item events (drag, split, take all, sort, rename, burn toggle) that name the previewed
    /// container before they are sent, so a drag does not visibly fail. A courtesy only: the server refuses them
    /// anyway, because the player holds no interaction with the container.
    /// </summary>
    public bool DropItemEvents { get; init; } = true;

    /// <summary>Null when the options are usable, else the sentence to throw.</summary>
    internal string? Problem()
    {
        if (!KeepOpen && (!double.IsFinite(Seconds) || Seconds <= 0 || Seconds > MaxSeconds))
            return "Seconds must be more than 0 and at most 600; use KeepOpen for longer.";
        if (!float.IsFinite(ReplicationTimeoutSeconds) || ReplicationTimeoutSeconds <= 0 || ReplicationTimeoutSeconds > MaxReplicationTimeoutSeconds)
            return "ReplicationTimeoutSeconds must be more than 0 and at most 30.";
        return null;
    }
}

/// <summary>What <see cref="IContainerPreview.Closed"/> reports.</summary>
public sealed class ContainerPreviewClosed
{
    internal ContainerPreviewClosed(ContainerId container, ContainerPreviewCloseReason reason, string message)
    {
        Container = container; Reason = reason; Message = message;
    }

    /// <summary>The previewed container.</summary>
    public ContainerId Container { get; }

    /// <summary>Why it closed.</summary>
    public ContainerPreviewCloseReason Reason { get; }

    /// <summary>A player-facing sentence (the server's refusal, for example); empty when the reason says it all.</summary>
    public string Message { get; }

    /// <inheritdoc />
    public override string ToString() => Reason + (Message.Length > 0 ? ": " + Message : "");
}

/// <summary>
/// A running <c>ctx.Inventory.Preview.Open</c>: the game's own container window for a container somewhere else,
/// read-only. Dispose to close it.
/// </summary>
public interface IContainerPreview : IDisposable
{
    /// <summary>The previewed container.</summary>
    ContainerId Container { get; }

    /// <summary>Where the preview is.</summary>
    ContainerPreviewState State { get; }

    /// <summary>Why it closed; <see cref="ContainerPreviewCloseReason.None"/> until then.</summary>
    ContainerPreviewCloseReason CloseReason { get; }

    /// <summary>The refusal or close sentence, player-facing; null until it closes.</summary>
    string? Reason { get; }

    /// <summary>Raised once on the main thread when the preview closes, for any reason but unload.</summary>
    event Action<ContainerPreviewClosed> Closed;
}

/// <summary>The client preview's internal phases; <see cref="Opening"/> shows as <see cref="ContainerPreviewState.Waiting"/>.</summary>
internal enum ContainerPreviewPhase
{
    Requesting = 0,
    Waiting = 1,
    Opening = 2,
    Open = 3,
    Closed = 4,
}

/// <summary>What one client preview tick saw. Game-free so the transitions are unit tested.</summary>
internal struct ContainerPreviewSample
{
    /// <summary>The client world is the one the preview started in and still exists.</summary>
    public bool SameWorld;
    /// <summary>The container's entity exists on this client.</summary>
    public bool TargetFound;
    /// <summary>The container's inventory entity exists on this client with its slot buffer.</summary>
    public bool InventoryReady;
    /// <summary>The game's open HUD menu is the container window, showing this target, with no interaction behind it.</summary>
    public bool WindowIsOurs;
    /// <summary>The game's open HUD menu is the container window (whatever it shows).</summary>
    public bool ContainerMenuOpen;
    /// <summary>The chest is on this client again, but as another entity than the one the window was opened on.</summary>
    public bool TargetReplaced;
    /// <summary>The game has some HUD menu open that is not ours.</summary>
    public bool OtherMenuOpen;
}

/// <summary>What a tick asks the runtime to do.</summary>
internal enum ContainerPreviewStep
{
    None = 0,
    /// <summary>Write the open request for the container window now.</summary>
    OpenWindow = 1,
    /// <summary>The preview closed this tick (<see cref="ContainerPreviewMachine.CloseReason"/>).</summary>
    Closed = 2,
}

/// <summary>
/// The client preview's state machine, game-free: Requesting → Waiting (server agreed, waiting for replication) →
/// Opening (open written, waiting for the window) → Open → Closed. Every timeout and close rule is here.
/// </summary>
internal sealed class ContainerPreviewMachine
{
    /// <summary>How long to wait for the server's answer.</summary>
    internal const double RequestTimeoutSeconds = 8;

    /// <summary>How long the window may take to appear after the open is written.</summary>
    internal const double OpenTimeoutSeconds = 2;

    /// <summary>How often a <see cref="ContainerPreviewOptions.KeepOpen"/> preview renews the server lease.</summary>
    internal const double RenewEverySeconds = 60;

    /// <summary>What each renewal asks the server for: three missed renewals before the server lets go.</summary>
    internal const double RenewGrantSeconds = 180;

    /// <summary>
    /// How long an open preview waits when the chest's inventory entity leaves the client (longer than the server's save
    /// suspension takes in the normal case) before it closes; meanwhile the window is held as it was.
    /// </summary>
    internal const double InventoryGraceSeconds = 20;

    /// <summary>True while an open preview's inventory is missing (the client holds the window's mapper).</summary>
    internal bool InventoryMissing => !double.IsNaN(_inventoryMissingSince);

    private readonly ContainerPreviewOptions _options;
    private readonly double _startedAt;
    private double _waitingSince;
    private double _openingSince;
    private double _nextRenew = double.PositiveInfinity;
    private double _inventoryMissingSince = double.NaN;

    internal ContainerPreviewMachine(ContainerPreviewOptions options, double now)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        var problem = options.Problem();
        if (problem != null) throw new ArgumentException(problem, nameof(options));
        _startedAt = now;
    }

    internal ContainerPreviewPhase Phase { get; private set; } = ContainerPreviewPhase.Requesting;

    internal ContainerPreviewState State => Phase switch
    {
        ContainerPreviewPhase.Requesting => ContainerPreviewState.Requesting,
        ContainerPreviewPhase.Waiting or ContainerPreviewPhase.Opening => ContainerPreviewState.Waiting,
        ContainerPreviewPhase.Open => ContainerPreviewState.Open,
        _ => ContainerPreviewState.Closed,
    };

    internal ContainerPreviewCloseReason CloseReason { get; private set; }

    internal string Message { get; private set; } = "";

    /// <summary>True when the preview wrote the open and has not closed: the window, if shown, is the preview's.</summary>
    internal bool OwnsWindow => Phase is ContainerPreviewPhase.Opening or ContainerPreviewPhase.Open;

    /// <summary>The seconds the server is asked to hold the lease for on the first request.</summary>
    internal double RequestSeconds => _options.KeepOpen ? RenewGrantSeconds : _options.Seconds;

    /// <summary>When a timed preview ends by time alone (from the request); +infinity with KeepOpen.</summary>
    internal double Deadline => _options.KeepOpen ? double.PositiveInfinity : _startedAt + _options.Seconds;

    /// <summary>The server's answer. A refusal closes with its sentence; an agreement starts waiting for replication.</summary>
    internal bool OnReply(double now, bool ok, string? message)
    {
        if (Phase != ContainerPreviewPhase.Requesting) return false;
        if (!ok) return Close(ContainerPreviewCloseReason.Refused, string.IsNullOrWhiteSpace(message) ? "The server refused the preview." : message!);
        Phase = ContainerPreviewPhase.Waiting;
        _waitingSince = now;
        if (_options.KeepOpen) _nextRenew = now + RenewEverySeconds;
        return true;
    }

    /// <summary>True when a KeepOpen preview should send a renewal now; call <see cref="MarkRenewed"/> after sending.</summary>
    internal bool RenewDue(double now) =>
        _options.KeepOpen
        && (Phase is ContainerPreviewPhase.Waiting or ContainerPreviewPhase.Opening or ContainerPreviewPhase.Open)
        && now >= _nextRenew;

    internal void MarkRenewed(double now) => _nextRenew = now + RenewEverySeconds;

    /// <summary>One tick. The order inside each phase is fixed: the world, then the target, then the window, then time.</summary>
    internal ContainerPreviewStep Step(double now, in ContainerPreviewSample s)
    {
        switch (Phase)
        {
            case ContainerPreviewPhase.Requesting:
                if (now - _startedAt >= RequestTimeoutSeconds)
                    return Closed(ContainerPreviewCloseReason.NoAnswer, "The server did not answer the preview request.");
                return ContainerPreviewStep.None;

            case ContainerPreviewPhase.Waiting:
                if (!s.SameWorld) return Closed(ContainerPreviewCloseReason.WorldChanged, "");
                if (now >= Deadline) return Closed(ContainerPreviewCloseReason.Timeout, "");
                if (s.TargetFound && s.InventoryReady && !s.OtherMenuOpen)
                {
                    Phase = ContainerPreviewPhase.Opening;
                    _openingSince = now;
                    return ContainerPreviewStep.OpenWindow;
                }
                if (now - _waitingSince >= _options.ReplicationTimeoutSeconds)
                {
                    if (s.TargetFound && s.InventoryReady)
                        return Closed(ContainerPreviewCloseReason.OpenFailed, "Another game menu is open.");
                    return Closed(ContainerPreviewCloseReason.ReplicationTimeout,
                        s.TargetFound ? "The container's inventory did not arrive." : "The container did not arrive.");
                }
                return ContainerPreviewStep.None;

            case ContainerPreviewPhase.Opening:
                if (!s.SameWorld) return Closed(ContainerPreviewCloseReason.WorldChanged, "");
                if (!s.TargetFound) return Closed(ContainerPreviewCloseReason.TargetGone, "");
                if (s.WindowIsOurs) { Phase = ContainerPreviewPhase.Open; return ContainerPreviewStep.None; }
                if (now - _openingSince >= OpenTimeoutSeconds) return Closed(ContainerPreviewCloseReason.OpenFailed, "The container window did not open.");
                return ContainerPreviewStep.None;

            case ContainerPreviewPhase.Open:
                if (!s.SameWorld) return Closed(ContainerPreviewCloseReason.WorldChanged, "");
                if (now >= Deadline) return Closed(ContainerPreviewCloseReason.Timeout, "");
                // The chest came back as another entity (it left the client, a save took the origin away for a moment): the
                // window is bound to the destroyed one. Reopening over an open container window is not a proven path, so end now.
                if (s.TargetReplaced) return Closed(ContainerPreviewCloseReason.TargetGone, "The container left this client.");
                if (!s.TargetFound || !s.InventoryReady)
                {
                    // The player closed the window (or opened another menu) meanwhile.
                    if (!s.ContainerMenuOpen) return Closed(ContainerPreviewCloseReason.WindowClosed, "");
                    // The chest or its contents left this client (a world save takes the streaming origin and the sync
                    // entries out for a moment, or the server let go): the window stays as it was, its mapper held, for a
                    // grace period, then the preview closes.
                    if (double.IsNaN(_inventoryMissingSince)) _inventoryMissingSince = now;
                    else if (now - _inventoryMissingSince >= InventoryGraceSeconds)
                        return Closed(ContainerPreviewCloseReason.TargetGone,
                            s.TargetFound ? "The container's contents stopped arriving." : "The container left this client.");
                    return ContainerPreviewStep.None;
                }
                _inventoryMissingSince = double.NaN;
                // Back (or never gone): the window must still be the container window on this chest. A chest that came back
                // as another entity is not the one the window shows, so the preview ends.
                if (!s.WindowIsOurs) return Closed(ContainerPreviewCloseReason.WindowClosed, "");
                return ContainerPreviewStep.None;

            default:
                return ContainerPreviewStep.None;
        }
    }

    /// <summary>
    /// Whether a close for <paramref name="reason"/> sends the server a close message. Every reason does but the ones
    /// where the server already let go or cannot hear it: a refusal (no lease was made), the server's own end, and a
    /// world change (the connection is gone). An unload and a request that got no answer in time do send it, so a
    /// lease the server made after all is released at once instead of at its deadline; the server ignores a close for
    /// a sequence it does not hold.
    /// </summary>
    internal static bool TellsServer(ContainerPreviewCloseReason reason) => reason is not (ContainerPreviewCloseReason.None
        or ContainerPreviewCloseReason.Refused or ContainerPreviewCloseReason.LeaseEnded or ContainerPreviewCloseReason.WorldChanged);

    /// <summary>Most a closed preview keeps its mapper guard while waiting for the window to go away.</summary>
    internal const double MapperGuardSeconds = 2;

    /// <summary>
    /// Whether a closed preview may drop its container-mapper guard: the world is gone, the game's open menu is no
    /// longer the container window on this chest, or <see cref="MapperGuardSeconds"/> passed since the close.
    /// </summary>
    internal static bool MapperGuardDone(bool worldAlive, bool windowStillOnTarget, double secondsSinceClose) =>
        !worldAlive || !windowStillOnTarget || secondsSinceClose >= MapperGuardSeconds;

    /// <summary>Close from outside the tick (Dispose, Escape, the server's end, unload). False when already closed.</summary>
    internal bool Close(ContainerPreviewCloseReason reason, string message)
    {
        if (Phase == ContainerPreviewPhase.Closed) return false;
        if (reason == ContainerPreviewCloseReason.None) throw new ArgumentOutOfRangeException(nameof(reason));
        Phase = ContainerPreviewPhase.Closed;
        CloseReason = reason;
        Message = message ?? "";
        return true;
    }

    private ContainerPreviewStep Closed(ContainerPreviewCloseReason reason, string message)
    {
        Close(reason, message);
        return ContainerPreviewStep.Closed;
    }
}

/// <summary>
/// When the preview stops the game's interact HUD from closing the container window (N2), game-free. The game closes an
/// interact-type menu every frame unless the local character holds a matching interaction; the preview has none by
/// design. It skips that system only while it owns the window, the window is the container window, no interaction
/// stands behind the window, and the game has no pending menu request of its own.
/// </summary>
internal static class ContainerPreviewHudGate
{
    internal static bool ShouldSkip(bool ownsWindow, bool opening, bool containerMenuOpen, bool mapperTargetOurs, bool mapperInteractNull, int pendingGameMenuRequests)
    {
        if (!ownsWindow || !containerMenuOpen || !mapperInteractNull || pendingGameMenuRequests > 0) return false;
        // While opening, the mapper may still hold the previous target for a frame.
        return mapperTargetOurs || opening;
    }
}

/// <summary>Who takes an Escape press while a container preview owns the window.</summary>
internal enum ContainerPreviewEscapeOwner
{
    NoPreview = 0,
    Dialog = 1,
    KeyCapture = 2,
    Chat = 3,
    Preview = 4,
}

/// <summary>
/// Escape precedence for a container preview: a framework dialog (or open dropdown) &gt; a key capture &gt; the game's
/// focused chat box &gt; the preview. The container window is a game menu, but the preview owns it, so Escape closes the
/// preview (and the window) rather than counting as "a game menu is open".
/// </summary>
internal static class ContainerPreviewEscape
{
    internal static ContainerPreviewEscapeOwner Decide(bool ownsWindow, bool dialogOpen, bool captureOwnsEscape, bool chatFocused)
    {
        if (!ownsWindow) return ContainerPreviewEscapeOwner.NoPreview;
        if (dialogOpen) return ContainerPreviewEscapeOwner.Dialog;
        if (captureOwnsEscape) return ContainerPreviewEscapeOwner.KeyCapture;
        if (chatFocused) return ContainerPreviewEscapeOwner.Chat;
        return ContainerPreviewEscapeOwner.Preview;
    }
}

/// <summary>What the server read about a preview request. Game-free so the authorization order is unit tested.</summary>
internal struct ContainerPreviewFacts
{
    public bool UserConnected;
    /// <summary>The server's <c>[ContainerPreview] RemotePreview</c> setting.</summary>
    public ContainerPreviewMode Mode;
    /// <summary>The server's game mode is PvE; null when it could not be read.</summary>
    public bool? IsPvE;
    public bool RateOk;
    public bool IdParsed;
    /// <summary>The id names a live container (<c>ctx.Inventory.TryFind</c>).</summary>
    public bool Found;
    /// <summary>The model has an inventory.</summary>
    public bool HasInventory;
    /// <summary>The model carries one of <see cref="ContainerPreviewMenus.OwnMenuComponents"/>: it has a menu of its own.</summary>
    public bool HasOwnMenu;
    /// <summary>The model is connected to a castle heart.</summary>
    public bool InCastle;
    public CastleRelation Relation;
    /// <summary>The server has the container's network id for the client.</summary>
    public bool HasNetworkId;
}

/// <summary>
/// Core's server rule for a container preview: the server's setting allows it, and the user is the owner or a clan
/// member of a plain container's castle, nothing else. Allies, strangers, stations, hearts and servant coffins are
/// refused. No mod can widen it.
///
/// <para>Clan membership is the whole access rule: the game has no per-container permission inside a clan (its ally
/// permissions grant only opening doors and building, and allies are refused here anyway), so a clan member may open
/// every chest of the clan's castles at the chest, and the preview follows that.</para>
/// </summary>
internal static class ContainerPreviewAuthorization
{
    /// <summary>Null to allow, else the player-facing refusal. The order is fixed.</summary>
    internal static string? Refusal(in ContainerPreviewFacts f)
    {
        if (!f.UserConnected) return "You are not connected.";
        if (ContainerPreviewSettings.Refusal(f.Mode, f.IsPvE) is { } off) return off;
        if (!f.RateOk) return "Too many preview requests; try again shortly.";
        if (!f.IdParsed) return "That is not a container id.";
        if (!f.Found) return "That container no longer exists.";
        if (!f.HasInventory) return "That has no inventory.";
        if (f.HasOwnMenu) return "Only plain containers (chests, braziers) can be previewed.";
        if (!f.InCastle) return "That container is not in a castle.";
        if (f.Relation is not (CastleRelation.Owner or CastleRelation.ClanMember))
            return "Only the castle's owner and clan can preview its containers.";
        if (!f.HasNetworkId) return "That container has no network id yet.";
        return null;
    }

    /// <summary>
    /// The refusal when the server's world-save hook is not in place: the inventory sync must be taken out around every
    /// world save, and without the hook the server never hears of one.
    /// </summary>
    internal const string SaveHookRefusal =
        "This server cannot share container contents right now: its world-save hook is not in place (see the server log).";

    /// <summary>The lease seconds the server grants for a requested length: clamped to (0, 600], 30 when unusable.</summary>
    internal static double GrantSeconds(double requested) =>
        !double.IsFinite(requested) || requested <= 0 ? 30 : Math.Min(requested, ContainerPreviewOptions.MaxSeconds);
}

/// <summary>The server's remote chest preview setting (<c>[ContainerPreview] RemotePreview</c>).</summary>
internal enum ContainerPreviewMode
{
    Off = 0,
    /// <summary>Allowed only while the server's game mode is PvE.</summary>
    PvEOnly = 1,
    On = 2,
}

/// <summary>
/// The server setting that turns remote chest preview off, on for PvE servers only, or on, game-free. Edited by an admin
/// in the Mods menu (Progenitor, On this server) over the admin channel; stored in the server's <c>progenitor.cfg</c>.
/// </summary>
internal static class ContainerPreviewSettings
{
    internal const string Section = "ContainerPreview";
    internal const string ModeKey = "RemotePreview";

    internal const string WordOff = "off";
    internal const string WordPvE = "pve";
    internal const string WordOn = "on";

    /// <summary>The stored words, in the order the row shows them.</summary>
    internal static readonly IReadOnlyList<string> Words = new[] { WordOff, WordPvE, WordOn };

    /// <summary>The default for a server that never set it. One line to change.</summary>
    internal const string DefaultWord = WordPvE;

    internal const string Label = "Remote chest preview";
    internal const string Help =
        "Lets players open the window of a chest in their own or their clan's castle from anywhere, to look inside. They "
        + "can never take, move or sort anything through it.";
    internal static readonly IReadOnlyList<string> Labels = new[] { "Off", "PvE servers only", "On" };
    internal static readonly IReadOnlyList<string> Descriptions = new[]
    {
        "Nobody can look into chests from afar on this server.",
        "Allowed while this server's game mode is PvE; off on PvP.",
        "Allowed on this server whatever its game mode.",
    };

    internal const string ConfigDescription =
        "Server. Remote chest preview: off, pve (only while the game mode is PvE) or on. Players see their own or their "
        + "clan's chests from afar, read-only. Edited in the Mods menu (Progenitor, On this server).";

    internal const string OffRefusal = "This server has remote chest preview turned off.";
    internal const string PvPRefusal = "This server allows remote chest preview only in PvE, and it is a PvP server.";

    /// <summary>The mode a stored word names; anything unknown is the default.</summary>
    internal static ContainerPreviewMode Parse(string? word) => (word ?? "").Trim().ToLowerInvariant() switch
    {
        WordOff => ContainerPreviewMode.Off,
        WordPvE => ContainerPreviewMode.PvEOnly,
        WordOn => ContainerPreviewMode.On,
        _ => Parse(DefaultWord),
    };

    /// <summary>Null when the setting allows a preview now, else the refusal. An unreadable game mode counts as not PvE.</summary>
    internal static string? Refusal(ContainerPreviewMode mode, bool? isPvE) => mode switch
    {
        ContainerPreviewMode.On => null,
        ContainerPreviewMode.PvEOnly => isPvE == true ? null : PvPRefusal,
        _ => OffRefusal,
    };
}

/// <summary>
/// What a plain container is not, game-free: the component names of every placed model with a menu of its own. The
/// server refuses a preview of anything carrying one of them (<c>ContainerPreviewServer.HasOwnMenu</c> checks each name;
/// a source test keeps the two lists equal).
/// </summary>
internal static class ContainerPreviewMenus
{
    internal static readonly IReadOnlyList<string> OwnMenuComponents = new[]
    {
        "CastleHeart", "Refinementstation", "WorkstationRecipesBuffer", "ResearchStation", "Salvagestation",
        "UnitSpawnerstation", "PrisonCell", "ServantCoffinstation", "ServantEquipment",
        "Forge_Shared", "FusionForge_Shared", "BloodMixer_Shared", "JewelCraftingStation", "Stables_Shared",
        "CastleArenaStation_Shared", "InventoryRouteStation",
    };
}
