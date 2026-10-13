using System;
using Progenitor.Units;

namespace Progenitor.Inputs;

/// <summary>The parts of the local player's input a lease can own. Each has one owner at a time.</summary>
[Flags]
public enum InputChannels
{
    None = 0,

    /// <summary>The aim point (<c>EntityInput.AimPosition</c> and its derived fields): facing and where aimed spells go.</summary>
    Aim = 1,

    /// <summary>The hovered unit (<c>EntityInput.HoveredEntity</c>): the target of targeted spells, feeding and interactions.</summary>
    Hover = 2,

    /// <summary>The walk direction (<c>EntityInput.Movement</c> and the command's <c>MovementInput</c>).</summary>
    Move = 4,

    /// <summary>The synced buttons (<c>EntityInput.State</c>): attacks, skills, spells, interact.</summary>
    Buttons = 8,

    All = Aim | Hover | Move | Buttons,
}

/// <summary>What a lease does while the real user is using the keyboard or mouse.</summary>
public enum UserPolicy
{
    /// <summary>
    /// Default. A channel pauses while the user uses its device (movement keys pause
    /// <see cref="InputChannels.Move"/>; mouse movement or clicks pause <see cref="InputChannels.Aim"/>
    /// and <see cref="InputChannels.Hover"/>; ability keys and mouse buttons pause
    /// <see cref="InputChannels.Buttons"/>) and resumes after <see cref="InputLease.YieldIdleSeconds"/>
    /// without activity. The lease is not ended.
    /// </summary>
    Yield,

    /// <summary>Keep writing while the user is active. For filming and deliberate assist modes; logged when acquired.</summary>
    Override,
}

/// <summary>Outcome of an acquire request.</summary>
public enum InputAcquireResult
{
    Acquired,

    /// <summary>Another lease holds at least one requested channel and <see cref="InputLeaseOptions.Preempt"/> was false. Nothing was acquired.</summary>
    Busy,

    /// <summary>No client world, or no single local character and input controller yet.</summary>
    NoLocalPlayer,

    /// <summary>The input layout check failed on this game build; input injection is disabled.</summary>
    LayoutRefused,

    /// <summary>Called in a server context. Input belongs to a client.</summary>
    ServerSide,

    /// <summary>An input write or readback threw earlier; injection is disabled for this Core generation. See <see cref="InputStatus.Issue"/>.</summary>
    Faulted,
}

/// <summary>Why a lease ended. Every lease ends exactly once.</summary>
public enum InputLeaseEnd
{
    /// <summary><see cref="InputLease.Dispose"/>.</summary>
    Released,

    /// <summary>The owning plugin unloaded (any reason).</summary>
    PluginUnloaded,

    /// <summary>Core was uninstalled or swapped.</summary>
    CoreUnloaded,

    /// <summary>The client world changed (a new world epoch).</summary>
    WorldChanged,

    /// <summary>The client world is gone (disconnect, back to the menu).</summary>
    Disconnected,

    /// <summary>The local character or the input controller entity was replaced or disappeared.</summary>
    CharacterChanged,

    /// <summary>Another lease was acquired with <see cref="InputLeaseOptions.Preempt"/> over one of its channels.</summary>
    Preempted,

    /// <summary>The user pressed the release-all keybind.</summary>
    UserPanic,

    /// <summary>The input layout check failed; injection is disabled.</summary>
    LayoutRefused,

    /// <summary>An input write or readback threw; injection is disabled for this Core generation.</summary>
    Faulted,
}

/// <summary>
/// The game's synced buttons (<c>ProjectM.SyncedButtonInputAction</c> in the client metadata),
/// mirrored so a plugin need not name an interop type. Values are the game's bits.
/// </summary>
[Flags]
public enum SyncedButtons : ushort
{
    None = 0,
    Interact = 1,
    Primary = 2,
    WeaponSkill1 = 4,
    TravelSkill = 8,
    CliffJump = 16,
    WeaponSkill2 = 32,
    OffensiveSpell = 64,
    DefensiveSpell = 128,
    UltimateSpell = 256,
    Ability4 = 512,
    Respawn = 1024,
    AutoMove = 2048,
    Interrupt = 4096,
}

/// <summary>An acquire request.</summary>
public readonly struct InputLeaseOptions
{
    /// <summary>Channels to own. Required; acquisition is all-or-nothing.</summary>
    public InputChannels Channels { get; init; }

    /// <summary>What the lease does while the user is active. Default <see cref="UserPolicy.Yield"/>.</summary>
    public UserPolicy User { get; init; }

    /// <summary>End any lease holding a requested channel instead of returning <see cref="InputAcquireResult.Busy"/>.</summary>
    public bool Preempt { get; init; }

    /// <summary>Shown in <see cref="InputStatus"/> and in logs.</summary>
    public string? Reason { get; init; }

    public InputLeaseOptions(InputChannels channels, UserPolicy user = UserPolicy.Yield, bool preempt = false, string? reason = null)
    {
        Channels = channels;
        User = user;
        Preempt = preempt;
        Reason = reason;
    }
}

/// <summary>
/// The newest outgoing input command the framework checked: what it carried after the framework's
/// patch, and whether the game had already copied the lease's intent into it before the patch
/// (<see cref="SourceMatched"/>). <see cref="Seen"/> is false until a command was checked.
/// </summary>
public readonly struct InputReadback
{
    public bool Seen { get; init; }

    /// <summary>The command's <c>ClientFrame</c>.</summary>
    public int ClientFrame { get; init; }

    /// <summary>Walk direction (x, z) in <c>MovementInput.MoveDirection</c>.</summary>
    public float MoveX { get; init; }

    public float MoveZ { get; init; }

    /// <summary><c>RawInput.AimPosition</c>.</summary>
    public UnitVec3 AimPosition { get; init; }

    /// <summary>First word of <c>RawInput.HoveredEntityNetworkId</c> (0 when nothing is hovered).</summary>
    public int HoveredNetworkId { get; init; }

    /// <summary><c>RawInput.HoveredEntity</c>: the client entity the game hovered under the cursor (null when nothing is).</summary>
    internal InputEntityRef HoveredEntity { get; init; }

    /// <summary><c>RawInput.State.InputsPressed</c>.</summary>
    public SyncedButtons Pressed { get; init; }

    /// <summary>True when the command already carried every held channel's intent before the framework patched it.</summary>
    public bool SourceMatched { get; init; }

    public override string ToString() =>
        !Seen ? "no command checked"
        : "frame " + ClientFrame + " move (" + MoveX.ToString("0.##") + ", " + MoveZ.ToString("0.##") + ") aim " + AimPosition
          + " hoverNet " + HoveredNetworkId + " pressed " + Pressed + (SourceMatched ? " (source matched)" : " (patched)");
}

/// <summary>Diagnostics for <c>ctx.Input</c>.</summary>
public readonly struct InputStatus
{
    /// <summary>False when the layout check failed; see <see cref="Issue"/>.</summary>
    public bool LayoutOk { get; init; }

    /// <summary>An input write or readback threw; injection is disabled until Core reloads. See <see cref="Issue"/>.</summary>
    public bool Faulted { get; init; }

    /// <summary>A client world with one local character and one input controller exists.</summary>
    public bool HasLocalPlayer { get; init; }

    /// <summary>New outgoing commands were seen in the last second. False on an unfocused normal client, whose input never reaches the server.</summary>
    public bool CommandsFlowing { get; init; }

    /// <summary>The game window has focus (<c>Application.isFocused</c>).</summary>
    public bool Focused { get; init; }

    /// <summary>Live leases.</summary>
    public int Leases { get; init; }

    /// <summary>Channels any lease holds.</summary>
    public InputChannels Held { get; init; }

    /// <summary>Channels paused because the user is using them.</summary>
    public InputChannels Suspended { get; init; }

    /// <summary>The newest outgoing command checked.</summary>
    public InputReadback LastSent { get; init; }

    /// <summary>Last problem, or null.</summary>
    public string? Issue { get; init; }

    public override string ToString() =>
        "layout " + (LayoutOk ? "ok" : "refused") + (Faulted ? ", FAULTED" : "") + ", player " + (HasLocalPlayer ? "yes" : "no")
        + ", commands " + (CommandsFlowing ? "flowing" : "not flowing") + ", focused " + (Focused ? "yes" : "no")
        + ", leases " + Leases + ", held " + Held + ", suspended " + Suspended + ", last " + LastSent
        + (Issue == null ? "" : ", issue: " + Issue);
}

/// <summary>A client-world entity id, game-free.</summary>
internal readonly struct InputEntityRef : IEquatable<InputEntityRef>
{
    public readonly int Index;
    public readonly int Version;

    public InputEntityRef(int index, int version)
    {
        Index = index;
        Version = version;
    }

    public bool IsNull => Index == 0 && Version == 0;

    public bool Equals(InputEntityRef other) => Index == other.Index && Version == other.Version;

    public override bool Equals(object? obj) => obj is InputEntityRef other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(Index, Version);

    public override string ToString() => Index + ":" + Version;
}

/// <summary>A <c>NetworkId</c>'s 12 bytes as three ints.</summary>
internal readonly struct InputNetworkId : IEquatable<InputNetworkId>
{
    public readonly int A, B, C;

    public InputNetworkId(int a, int b, int c)
    {
        A = a;
        B = b;
        C = c;
    }

    public bool IsZero => A == 0 && B == 0 && C == 0;

    public bool Equals(InputNetworkId other) => A == other.A && B == other.B && C == other.C;

    public override bool Equals(object? obj) => obj is InputNetworkId other && Equals(other);

    public override int GetHashCode() => HashCode.Combine(A, B, C);
}

/// <summary>User device activity sampled this frame.</summary>
[Flags]
internal enum UserActivity
{
    None = 0,

    /// <summary>The pointer moved or a mouse button is held.</summary>
    Pointer = 1,

    /// <summary>A movement key is held.</summary>
    MoveKeys = 2,

    /// <summary>An ability key or mouse button is held.</summary>
    ButtonKeys = 4,
}

/// <summary>
/// What to write into <c>EntityInput</c> this frame (controller's and character's). Fields are
/// written only when their flag is set; <see cref="Pressed"/> is OR-ed into the user's bits.
/// </summary>
internal struct InputWrite
{
    public bool WriteMove;
    public float MoveX, MoveZ;

    public bool WriteAim;
    public UnitVec3 AimPosition, AimPlane, AimDirection;
    public float AimLength;

    public bool WriteHover;
    public InputEntityRef HoverEntity;
    public InputNetworkId HoverNetworkId;

    public ushort Pressed;

    /// <summary>Rotation for the command's <c>RotationInput.TargetDirection</c>, when set.</summary>
    public bool WriteRotation;
    public UnitVec3 Rotation;

    public bool Any => WriteMove || WriteAim || WriteHover || Pressed != 0;
}

/// <summary>
/// One outgoing <c>InputCommandBufferElement</c>'s fields the framework reads and may patch.
/// The runtime fills it from the buffer and writes back every field when <see cref="Patched"/>.
/// </summary>
internal struct InputCommandView
{
    public int ClientFrame;
    public float MoveX, MoveZ;             // MovementInput.MoveDirection
    public UnitVec3 Rotation;              // RotationInput.TargetDirection
    public float RawMoveX, RawMoveZ;       // RawInput.Movement
    public UnitVec3 AimPosition, AimPlane, AimDirection;
    public float AimLength;
    public InputEntityRef HoverEntity;
    public InputNetworkId HoverNetworkId;
    public ushort Up, Down, Pressed;
    public bool Patched;
}
