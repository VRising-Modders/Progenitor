using System;
using Progenitor.Ui;

namespace Progenitor.Inventory;

/// <summary>
/// What <c>ctx.Inventory.ReopenLast</c> did (client). <see cref="Started"/> is only ever the call's own answer: the
/// reopen then runs for about a second and its <c>done</c> callback gets one of the other values.
/// </summary>
public enum ContainerReopenResult
{
    /// <summary>The reopen is running: the game's interaction with the chest or station is being repeated.</summary>
    Started = 0,

    /// <summary>The window is open again on the same chest or station.</summary>
    Opened = 1,

    /// <summary>The window was still open; nothing was pressed.</summary>
    AlreadyOpen = 2,

    /// <summary>No chest or station window was opened in this world yet (or the last one was too long ago).</summary>
    NothingToReopen = 3,

    /// <summary>The chest or station is no longer on this client (destroyed, or out of streaming range).</summary>
    Gone = 4,

    /// <summary>The character moved away from where it stood with the window open.</summary>
    Moved = 5,

    /// <summary>Another game window stayed open, or a camera preview kept running, so nothing was pressed.</summary>
    MenuOpen = 6,

    /// <summary>Another input lease holds the hover or the buttons, so nothing was pressed.</summary>
    InputBusy = 7,

    /// <summary>Interact was pressed and the window did not open: out of range, refused by the server, or a mod's open gate asked first.</summary>
    NotOpened = 8,

    /// <summary>A newer reopen replaced this one, or the world changed, or Core unloaded.</summary>
    Cancelled = 9,

    /// <summary>Not in a game world on the client (the server, the title screen).</summary>
    NotInGame = 10,
}

/// <summary>
/// The chest or station window the player had open most recently (<c>ctx.Inventory.LastWindow</c>, client): the one
/// <c>ReopenLast</c> would open again. Only windows the player opened at the object count; a remote container
/// preview's window never does.
/// </summary>
public sealed class ContainerWindowInfo
{
    internal ContainerWindowInfo(VanillaPanelKind kind, int menuType, int entityIndex, int entityVersion,
        int networkIdIndex, int networkIdGeneration, bool isOpen, double secondsSinceSeen)
    {
        Kind = kind; MenuType = menuType; EntityIndex = entityIndex; EntityVersion = entityVersion;
        NetworkIdIndex = networkIdIndex; NetworkIdGeneration = networkIdGeneration; IsOpen = isOpen; SecondsSinceSeen = secondsSinceSeen;
    }

    /// <summary>Which window: <see cref="VanillaPanelKind.Container"/>, a station kind or the castle heart.</summary>
    public VanillaPanelKind Kind { get; }

    /// <summary>The game's HUD menu number for that window (3 is the container window).</summary>
    public int MenuType { get; }

    /// <summary>The object's client entity index (this world only; never a stable id).</summary>
    public int EntityIndex { get; }

    /// <summary>The object's client entity version.</summary>
    public int EntityVersion { get; }

    /// <summary>The object's network id index, the words a mod can send to its server.</summary>
    public int NetworkIdIndex { get; }

    /// <summary>The object's network id generation.</summary>
    public int NetworkIdGeneration { get; }

    /// <summary>True when that window is open now.</summary>
    public bool IsOpen { get; }

    /// <summary>Seconds since the window was last seen open (0 while it is open).</summary>
    public double SecondsSinceSeen { get; }

    /// <inheritdoc />
    public override string ToString() =>
        Kind + " " + EntityIndex + ":" + EntityVersion + " net=" + NetworkIdIndex + ":" + NetworkIdGeneration
        + (IsOpen ? " open" : " closed " + SecondsSinceSeen.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture) + "s ago");
}

/// <summary>The rules of returning to the last chest or station window, game-free.</summary>
internal static class ContainerReturnRules
{
    /// <summary>
    /// How far (meters, on the ground plane) the character may have moved from where it stood with the window open. The
    /// player opened the window from that spot, so from there the game's own interaction reaches the object; farther, a
    /// reopen is not attempted.
    /// </summary>
    internal const float MaxMoveMeters = 1.0f;

    /// <summary>How long a remembered window stays reopenable after it closed.</summary>
    internal const double RememberSeconds = 600;

    /// <summary>True when the character moved too far from where it stood (<see cref="MaxMoveMeters"/>).</summary>
    internal static bool Moved(float stoodX, float stoodZ, float nowX, float nowZ)
    {
        float dx = nowX - stoodX, dz = nowZ - stoodZ;
        if (!float.IsFinite(dx) || !float.IsFinite(dz)) return true;
        return dx * dx + dz * dz > MaxMoveMeters * MaxMoveMeters;
    }

    /// <summary>
    /// The call's own answer, in order: a game world, a remembered window not too old, the object still on this client,
    /// the character still where it stood. <see cref="ContainerReopenResult.Started"/> when all hold.
    /// </summary>
    internal static ContainerReopenResult Check(bool inGame, bool remembered, double secondsSinceSeen, bool targetPresent, bool moved)
    {
        if (!inGame) return ContainerReopenResult.NotInGame;
        if (!remembered || !(secondsSinceSeen <= RememberSeconds)) return ContainerReopenResult.NothingToReopen;
        if (!targetPresent) return ContainerReopenResult.Gone;
        if (moved) return ContainerReopenResult.Moved;
        return ContainerReopenResult.Started;
    }

    /// <summary>
    /// Whether a container preview that took the place of the player's window opens that window again when it closes.
    /// Not when a newer preview replaced it (that one returns instead), the world changed, or the plugin or Core unloaded.
    /// A preview the game closed because the player opened something else is handled by the reopen itself, which gives up
    /// when another game window stays open.
    /// </summary>
    internal static bool ReturnsAfterPreview(ContainerPreviewCloseReason reason) => reason is not (
        ContainerPreviewCloseReason.None or ContainerPreviewCloseReason.Replaced
        or ContainerPreviewCloseReason.WorldChanged or ContainerPreviewCloseReason.Unloaded);

    /// <summary>
    /// Whether the window open now is one to remember: a chest or station window the player opened at the object. A
    /// container window with no interaction behind it is a remote preview's, never remembered.
    /// </summary>
    internal static bool Remembers(VanillaPanelKind kind, bool hasInteraction, bool previewOwnsWindow)
    {
        if (kind is VanillaPanelKind.None or VanillaPanelKind.Character) return false;
        if (kind == VanillaPanelKind.Container && (!hasInteraction || previewOwnsWindow)) return false;
        return true;
    }
}

/// <summary>What one reopen tick saw (<see cref="ContainerReopenPlan"/>).</summary>
internal struct ContainerReopenSample
{
    /// <summary>The client world the reopen started in still exists.</summary>
    public bool SameWorld;
    /// <summary>The object is still on this client (the same network id).</summary>
    public bool TargetPresent;
    /// <summary>The character moved too far from where it stood.</summary>
    public bool Moved;
    /// <summary>The game's window shows the object now.</summary>
    public bool WindowOnTarget;
    /// <summary>Some game HUD menu is open (any, the remembered window included).</summary>
    public bool AnyMenuOpen;
    /// <summary>A camera preview is running (it moves the player's view; the reopen waits for it to end).</summary>
    public bool CameraPreview;
}

/// <summary>What the runtime does this frame.</summary>
internal enum ContainerReopenStep
{
    /// <summary>Nothing yet.</summary>
    Wait = 0,
    /// <summary>Take the input lease and hover the object.</summary>
    StartHover = 1,
    /// <summary>Press Interact (once, held <see cref="ContainerReopenPlan.PressSeconds"/>).</summary>
    Press = 2,
    /// <summary>Done: hand the input back; <see cref="ContainerReopenPlan.Result"/> says how it went.</summary>
    Finish = 3,
}

/// <summary>
/// One reopen, game-free: let the frame settle (a close the same key press asked for lands a frame later), wait for the
/// game's windows to close and a camera preview to end, hover the object for a few frames so the hover reaches the
/// outgoing command, press Interact once, then wait for the window. The hover-then-press order is the Dev bot driver's
/// and the container open gate's repeat.
/// </summary>
internal sealed class ContainerReopenPlan
{
    /// <summary>Frames before the first look: a window closing on this frame's key press is gone by then.</summary>
    internal const int SettleFrames = 2;

    /// <summary>How long another game window may stay open before the reopen gives up.</summary>
    internal const double MenuWaitSeconds = 1.5;

    /// <summary>How long a camera preview may keep running before the reopen gives up.</summary>
    internal const double CameraWaitSeconds = 60;

    /// <summary>Frames of hover before the press.</summary>
    internal const int HoverFrames = 3;

    /// <summary>How long Interact is held.</summary>
    internal const float PressSeconds = 0.4f;

    /// <summary>How long after the press the window may take to open.</summary>
    internal const double OpenWaitSeconds = 1.5;

    private enum Phase { Settle, WaitMenu, Hover, Pressed, Done }

    private Phase _phase = Phase.Settle;
    private int _frames;
    private double _since;

    internal ContainerReopenPlan(double now) => _since = now;

    /// <summary>The outcome once <see cref="Done"/>; <see cref="ContainerReopenResult.Started"/> before.</summary>
    internal ContainerReopenResult Result { get; private set; } = ContainerReopenResult.Started;

    internal bool Done => _phase == Phase.Done;

    /// <summary>True once Interact was pressed.</summary>
    internal bool PressedEver => _pressedEver;

    private bool _pressedEver;

    /// <summary>End now with <paramref name="result"/> (the lease was refused, a newer reopen, unload).</summary>
    internal ContainerReopenStep End(ContainerReopenResult result)
    {
        if (_phase == Phase.Done) return ContainerReopenStep.Wait;
        _phase = Phase.Done;
        Result = result;
        return ContainerReopenStep.Finish;
    }

    /// <summary>One frame.</summary>
    internal ContainerReopenStep Step(double now, in ContainerReopenSample s)
    {
        if (_phase == Phase.Done) return ContainerReopenStep.Wait;
        if (!s.SameWorld) return End(ContainerReopenResult.Cancelled);
        if (_phase == Phase.Pressed && s.WindowOnTarget) return End(ContainerReopenResult.Opened);
        if (!s.TargetPresent) return End(ContainerReopenResult.Gone);
        if (s.Moved) return End(ContainerReopenResult.Moved);
        switch (_phase)
        {
            case Phase.Settle:
                if (++_frames < SettleFrames) return ContainerReopenStep.Wait;
                if (s.WindowOnTarget) return End(ContainerReopenResult.AlreadyOpen);
                _phase = Phase.WaitMenu;
                _since = now;
                goto case Phase.WaitMenu;

            case Phase.WaitMenu:
                if (s.WindowOnTarget) return End(ContainerReopenResult.AlreadyOpen);
                if (s.CameraPreview)
                {
                    if (now - _since >= CameraWaitSeconds) return End(ContainerReopenResult.MenuOpen);
                    return ContainerReopenStep.Wait;
                }
                if (s.AnyMenuOpen)
                {
                    if (now - _since >= MenuWaitSeconds) return End(ContainerReopenResult.MenuOpen);
                    return ContainerReopenStep.Wait;
                }
                _phase = Phase.Hover;
                _frames = 0;
                return ContainerReopenStep.StartHover;

            case Phase.Hover:
                // Something else opened while hovering (the player interacted with another object): not ours to press.
                if (s.AnyMenuOpen || s.CameraPreview) return End(ContainerReopenResult.MenuOpen);
                if (++_frames < HoverFrames) return ContainerReopenStep.Wait;
                _phase = Phase.Pressed;
                _pressedEver = true;
                _since = now;
                return ContainerReopenStep.Press;

            case Phase.Pressed:
                if (now - _since >= OpenWaitSeconds) return End(ContainerReopenResult.NotOpened);
                return ContainerReopenStep.Wait;
        }
        return ContainerReopenStep.Wait;
    }
}
