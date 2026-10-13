namespace Progenitor.Units;

/// <summary>
/// Exclusive Follow / GoTo field set. Offsets are the live overlay (evidence 33 / 46)
/// and match the server metadata for <c>Follower</c>, <c>AiMove_Server</c>
/// and <c>AiTargettingMode</c>. GoTo after Follow must clear
/// this whole Follow set before writing ApproachPosition; Follow after GoTo must clear
/// the goal the same way. The fake runtime records these fields so a regression fails.
/// </summary>
internal static class UnitSteering
{
    /// <summary><c>Follower.Followed</c> (<c>ModifiableEntity._Value</c>) @0x0.</summary>
    public const int FollowerFollowedOffset = Layout.FollowerFollowedOffset;

    /// <summary><c>Follower.ModeModifiable</c> @0x18 (24).</summary>
    public const int FollowerModeOffset = Layout.FollowerModeOffset;

    /// <summary><c>Follower.Stationary</c> @0x1C (28). Spike writes 0 to walk.</summary>
    public const int FollowerStationaryOffset = Layout.FollowerStationaryOffset;

    /// <summary><c>FollowMode.Unit</c>.</summary>
    public const int FollowModeUnit = Layout.FollowModeUnit;

    /// <summary><c>AiMove_Server.TargetPosition</c> xz @0x0.</summary>
    public const int AiMoveTargetPositionOffset = Layout.AiMoveTargetPositionOffset;

    /// <summary><c>AiMove_Server.TargetEntity_Movement</c> @0x18 (24). ApproachEntity writes the follow target here.</summary>
    public const int AiMoveTargetEntityMovementOffset = Layout.AiMoveTargetEntityMovementOffset;

    /// <summary><c>AiMove_Server.TargettingMode</c> @0x48 (72).</summary>
    public const int AiMoveTargettingModeOffset = Layout.AiMoveTargettingModeOffset;

    /// <summary><c>AiTargettingMode.None</c> — ApproachEntity.Abort writes this byte when ForceStopOnExit.</summary>
    public const byte TargettingNone = Layout.AiTargettingModeNone;

    /// <summary><c>AiTargettingMode.Position</c> — ApproachPosition.Tick.</summary>
    public const byte TargettingPosition = Layout.AiTargettingModePosition;

    /// <summary><c>AiTargettingMode.Entity</c> — Follow's ApproachEntity.Tick.</summary>
    public const byte TargettingEntity = Layout.AiTargettingModeEntity;

    /// <summary><c>GenericEnemyState.Idle = 2</c>. Posted on GoTo so the Follow leaf stops rewriting AiMove.</summary>
    public const int BtIdle = Layout.GenericEnemyStateIdle;

    /// <summary><c>GenericEnemyState.Follow = 0x100</c>. Posted on Follow (evidence 33).</summary>
    public const int BtFollow = Layout.GenericEnemyStateFollow;
}
