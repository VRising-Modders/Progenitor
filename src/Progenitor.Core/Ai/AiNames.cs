using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ai;

/// <summary>
/// Display names for enum values the Ai module prints. Names only: no decision in Core reads
/// them, and the values the module acts on come from <see cref="Layout"/>.
/// </summary>
internal static class AiNames
{
    /// <summary>Name of a <c>BehaviourTreeNodeType</c> value, or <c>type#N</c>.</summary>
    internal static string NodeType(int value) => Lookup(NodeTypes, value, "type#");

    /// <summary>Name of a <c>ConditionSource</c> value, or <c>source#N</c>.</summary>
    internal static string ConditionSource(int value) => Lookup(ConditionSources, value, "source#");

    /// <summary><c>ConditionTypes</c> as an operator.</summary>
    internal static string Comparison(int value) => value switch
    {
        0 => "==",
        1 => ">",
        2 => ">=",
        3 => "<",
        4 => "<=",
        5 => "!=",
        _ => "cmp#" + value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary><c>ConditionTarget</c> name.</summary>
    internal static string ConditionTarget(int value) => value switch
    {
        0 => "Owner",
        1 => "Target",
        2 => "Self",
        3 => "Unit_Mount",
        4 => "Unit_Mounter",
        5 => "MinionMaster",
        _ => "target#" + value.ToString(CultureInfo.InvariantCulture),
    };

    /// <summary><c>GenericEnemyState</c> flags as names (server metadata).</summary>
    internal static string State(int value)
    {
        if (value == 0) return "Initialize";
        var sb = new StringBuilder();
        int rest = value;
        foreach (var (bit, name) in States)
        {
            if ((value & bit) != bit) continue;
            if (sb.Length > 0) sb.Append('|');
            sb.Append(name);
            rest &= ~bit;
        }
        if (rest != 0)
        {
            if (sb.Length > 0) sb.Append('|');
            sb.Append("0x").Append(rest.ToString("X", CultureInfo.InvariantCulture));
        }
        return sb.ToString();
    }

    /// <summary><c>AB_Wendigo_FrostNova_AbilityGroup</c> becomes <c>FrostNova</c>.</summary>
    internal static string Short(string name)
    {
        if (string.IsNullOrEmpty(name)) return "";
        string s = name;
        if (s.EndsWith("_AbilityGroup", System.StringComparison.Ordinal)) s = s.Substring(0, s.Length - "_AbilityGroup".Length);
        if (s.StartsWith("AB_", System.StringComparison.Ordinal))
        {
            int second = s.IndexOf('_', 3);
            if (second > 0 && second < s.Length - 1) s = s.Substring(second + 1);
        }
        return s;
    }

    /// <summary>Case-insensitive match of a node type name to its value; -1 when unknown.</summary>
    internal static int NodeTypeValue(string name)
    {
        foreach (var (value, n) in NodeTypes)
            if (string.Equals(n, name, System.StringComparison.OrdinalIgnoreCase)) return value;
        return -1;
    }

    private static string Lookup((int Value, string Name)[] table, int value, string prefix)
    {
        foreach (var (v, n) in table)
            if (v == value) return n;
        return prefix + value.ToString(CultureInfo.InvariantCulture);
    }

    private static readonly (int Bit, string Name)[] States =
    {
        (Layout.GenericEnemyStateIdle, "Idle"), (4, "Alert"), (Layout.GenericEnemyStateCombat, "Combat"), (64, "Return"), (128, "Flee"), (Layout.GenericEnemyStateFollow, "Follow"),
        (2048, "Imprisoned"), (4096, "Servant_FindWorkstation"), (8192, "Servant_WalkToWorkstation"),
        (16384, "Servant_TeleportToWorkstation"), (32768, "Villager_Cover"), (262144, "Relocate_Unstuck"),
        (524288, "Idle_Horse"), (1048576, "Relocate_CombatArea"),
    };

    // BehaviourTreeNodeType, server metadata. Display names only.
    private static readonly (int Value, string Name)[] NodeTypes =
    {
        (0, "Root"),
        (1, "Sequence"),
        (2, "Selector"),
        (3, "Random"),
        (4, "RandomSelector"),
        (5, "Inverter"),
        (6, "Succeeder"),
        (7, "Failer"),
        (8, "Repeater"),
        (9, "Parallel"),
        (10, "Transition"),
        (11, "Bool_IsTrueInstant"),
        (12, "Bool_IsTrueMonitor"),
        (13, "DamageTakenInstant"),
        (14, "DamageTakenMonitor"),
        (15, "DeaggroReasonEqualsInstant"),
        (16, "DeaggroReasonEqualsMonitor"),
        (17, "EntityInConeInstant"),
        (18, "EntityInConeMonitor"),
        (19, "EntityInPathfindingLineOfSightOrWillBeSoonInstant"),
        (20, "EntityInPathfindingLineOfSightOrWillBeSoonMonitor"),
        (21, "Float_GreaterThanInstant"),
        (22, "Float_GreaterThanMonitor"),
        (23, "Float_LessThanInstant"),
        (24, "Float_LessThanMonitor"),
        (25, "HasAggroTargetInstant"),
        (26, "HasAggroTargetMonitor"),
        (27, "HasAlertTargetInstant"),
        (28, "HasAlertTargetMonitor"),
        (29, "HasFollowTargetInstant"),
        (30, "HasFollowTargetMonitor"),
        (31, "HasPathInstant"),
        (32, "HasPathMonitor"),
        (33, "HasStablesHitchPointInstant"),
        (34, "HasStablesHitchPointMonitor"),
        (35, "HealthPercentageLessThanInstant"),
        (36, "HealthPercentageLessThanMonitor"),
        (37, "EntityInLineOfSightInstant"),
        (38, "EntityInLineOfSightMonitor"),
        (39, "PositionInLineOfSightInstant"),
        (40, "PositionInLineOfSightMonitor"),
        (41, "EntityInPathfindingLineOfSightInstant"),
        (42, "EntityInPathfindingLineOfSightMonitor"),
        (43, "PositionInPathfindingLineOfSightInstant"),
        (44, "PositionInPathfindingLineOfSightMonitor"),
        (45, "Int_EqualsInstant"),
        (46, "Int_EqualsMonitor"),
        (47, "Int_BlackboardEqualsInstant"),
        (48, "Int_BlackboardEqualsMonitor"),
        (49, "Int_GreaterThanInstant"),
        (50, "Int_GreaterThanMonitor"),
        (51, "Int_LessThanInstant"),
        (52, "Int_LessThanMonitor"),
        (53, "IsBehindInstant"),
        (54, "IsBehindMonitor"),
        (55, "IsBlackboardEntityNullInstant"),
        (56, "IsBlackboardEntityNullMonitor"),
        (57, "IsBusyInstant"),
        (58, "IsBusyMonitor"),
        (59, "IsFollowerStationaryInstant"),
        (60, "IsFollowerStationaryMonitor"),
        (61, "IsInFollowModeInstant"),
        (62, "IsInFollowModeMonitor"),
        (63, "IsMovingInstant"),
        (64, "IsMovingMonitor"),
        (65, "IsPatrollingInstant"),
        (66, "IsPatrollingMonitor"),
        (67, "IsServantDecayingInstant"),
        (68, "IsServantDecayingMonitor"),
        (69, "IsTravellingInstant"),
        (70, "IsTravellingMonitor"),
        (71, "IsWithinRangeInstant"),
        (72, "IsWithinRangeMonitor"),
        (73, "ShouldCombatAreaRelocateInstant"),
        (74, "ShouldCombatAreaRelocateMonitor"),
        (75, "ShouldWanderWhenIdleInstant"),
        (76, "ShouldWanderWhenIdleMonitor"),
        (77, "TimePassedSinceInstant"),
        (78, "TimePassedSinceMonitor"),
        (79, "TimePassedSinceBlackboardInstant"),
        (80, "TimePassedSinceBlackboardMonitor"),
        (81, "AlertAllies"),
        (82, "ApproachEntity"),
        (83, "ApproachPosition"),
        (84, "PatrolApproachPosition"),
        (85, "AttackEntityCastOption"),
        (86, "Bool_Set"),
        (87, "ClearAggro"),
        (88, "ClearEntity"),
        (89, "CreateIdleInteractionPointReachedEvent"),
        (90, "CurrentPositionToBlackboard"),
        (91, "Delay"),
        (92, "DelayUntilDamageTakenConstant"),
        (93, "DelayUntilDamageTaken"),
        (94, "DestroyEntityIfExists"),
        (95, "ElapsedTimeToBlackboard"),
        (96, "EntityMoveSpeedToBlackboard"),
        (97, "EntityPositionToBlackboard"),
        (98, "EntityVelocityToBlackboard"),
        (99, "EvaluateCastOptions"),
        (100, "Fail"),
        (101, "FindFleeDistance"),
        (102, "FindPathAwayFromPosition"),
        (103, "FindPathToPosition"),
        (104, "Float2_Set"),
        (105, "Float3_Set"),
        (106, "Float3_Add"),
        (107, "Float3_Subtract"),
        (108, "Float3_Dot"),
        (109, "Float3_LengthSqToBlackboard"),
        (110, "Float3_Copy"),
        (111, "Float_Set"),
        (112, "Float_Add"),
        (113, "Float_Subtract"),
        (114, "FollowOffsetToBlackboard"),
        (115, "FollowPath"),
        (116, "GenericCombatMovement"),
        (117, "GetCastleWorkstationInteractPointData"),
        (118, "HealthChangePercentage"),
        (119, "HealthChangeAbsolute"),
        (120, "TryGetIdleInteractionPointTransform"),
        (121, "HasValidIdleInteractionPoint"),
        (122, "IdleInteractorSlideIntoPosition"),
        (123, "InitializeGenericFleeing"),
        (124, "Int_Set"),
        (125, "Int_Add"),
        (126, "Int_Copy"),
        (127, "IsInStateInstant"),
        (128, "LookAtEntity"),
        (129, "LookAtPosition"),
        (130, "LookAlongRotation"),
        (131, "LookAlongSpawnRotation"),
        (132, "MapCollisionRadiusToBlackboard"),
        (133, "MinionCountToBlackboard"),
        (134, "PatrolTeleport"),
        (135, "TryGetPointOfInterestData"),
        (136, "TrySetEntityToClosestPointOfInterestOfType"),
        (137, "Print"),
        (138, "PrintBlackboardFloat"),
        (139, "PrintBlackboardFloat2"),
        (140, "PrintBlackboardFloat3"),
        (141, "PrintBlackboardFloat4"),
        (142, "PrintBlackboardInt"),
        (143, "PrintBlackboardInt2"),
        (144, "PrintBlackboardInt3"),
        (145, "PrintBlackboardInt4"),
        (146, "PrintBlackboardBool"),
        (147, "PrintBlackboardEntity"),
        (148, "PrintExecutionTrace"),
        (149, "PullPath"),
        (150, "RandomDoubleToBlackboard"),
        (151, "RandomFloatToBlackboard"),
        (152, "RandomIntToBlackboard"),
        (153, "Relocate_CanExit"),
        (154, "Relocate_CombatArea_FindPos"),
        (155, "Relocate_ShouldEarlyOut"),
        (156, "Relocate_Stuck_FindPos"),
        (157, "RemoveEntityFromAggroLists"),
        (158, "ResetAllCooldowns"),
        (159, "SetCombatAreaRelocationDistance"),
        (160, "SetPosToRandomTileAroundBlackboardPos"),
        (161, "ShouldResetFollowState"),
        (162, "SpawnPositionToBlackboard"),
        (163, "Succeed"),
        (164, "SucceedAfterTicks"),
        (165, "Teleport"),
        (166, "TryFindFirstWalkable"),
        (167, "TryFindServantRoamPath"),
        (168, "TryGetStablesHitchPointTransform"),
        (169, "TrySetEntityToClosestResourceNode"),
        (170, "TrySetEntityToClosestWithRelation"),
        (171, "TrySetEntityToFollowTarget"),
        (172, "TrySetEntityToHighestPrioAggroTargetInRange"),
        (173, "TrySetEntityToPrimaryAggroTarget"),
        (174, "TrySetEntityToPrimaryAlertTarget"),
        (175, "TrySetEntityToPrisonCell"),
        (176, "TrySetPosToValidPosAroundEntity"),
    };

    // ConditionSource, server metadata. Display names only.
    private static readonly (int Value, string Name)[] ConditionSources =
    {
        (0, "Health"),
        (1, "HealthFactor"),
        (4, "Blood"),
        (5, "BloodFactor"),
        (6, "UnitType"),
        (7, "HasBuff"),
        (8, "IsAlly"),
        (9, "IsAlive"),
        (10, "IsOwner"),
        (11, "BloodConsumeQuality"),
        (12, "DoorCanBeClosed"),
        (13, "HasProgression"),
        (14, "CanBeCharmed"),
        (15, "IsWaypointUnlocked"),
        (16, "IsUserOwner"),
        (17, "HasUserOwner"),
        (18, "HasRespawnPointOwner"),
        (19, "HasItems"),
        (20, "IsMounted"),
        (21, "HasBuffOwnedByNonOwner"),
        (22, "IsPrefab"),
        (23, "IsDay"),
        (24, "Resistance_Fire"),
        (25, "Resistance_Garlic"),
        (26, "Resistance_Holy"),
        (27, "Resistance_Silver"),
        (28, "Age"),
        (29, "IsWounded"),
        (30, "TimeSinceAbilityCasted"),
        (31, "LastAbilityCasted"),
        (32, "MinionCount"),
        (33, "IsCasting"),
        (34, "IsSelf"),
        (35, "DayTimeSpanActive"),
        (36, "HasEquippedWeaponOfType"),
        (37, "DistanceBetweenTargetAndSelf"),
        (39, "IsShapeShifted"),
        (40, "MaterialCategory"),
        (41, "IsEnemy"),
        (42, "IsInAggroList"),
        (43, "AbilityCastCount_Total"),
        (44, "IsFeedWounded"),
        (45, "DamageTakenSince"),
        (46, "TimeInCurrentState"),
        (47, "BlockFeed"),
        (50, "IsPlayerCharacter"),
        (51, "IsAiResourceNode"),
        (53, "MainCategory"),
        (54, "AbilityCastCount_InARow"),
        (55, "HasPermission"),
        (56, "AggroListCount"),
        (57, "HasAnyEquippedItem"),
        (58, "InventoryItemCount"),
        (59, "HasTeleportBoundItem"),
        (60, "HasBuffWithAge"),
        (61, "WeaponLevel"),
        (62, "HasBuffOwnedByOwner"),
        (63, "FollowersCount"),
        (64, "IsFlying"),
        (65, "IsFed"),
        (66, "IsInUnitTeam"),
        (67, "IsConfuseDummy"),
        (68, "IsServant"),
        (70, "CanRepair"),
        (71, "OwnerBoundToTarget"),
        (72, "PvEEnabled"),
        (73, "PvPEnabled"),
        (74, "CanLootEnemyContainersEnabled"),
        (75, "CanInteractWithEnemyCastleHeart"),
        (76, "HasMaxMinions"),
        (77, "RandomValueBetween0And1"),
        (78, "IsVBlood"),
        (79, "HasBuffResistances"),
        (80, "UnitHealthType"),
        (81, "BuffModificationActive"),
        (82, "IsAiPointOfInterestOfType"),
        (83, "BuffStacks"),
        (84, "HasSpellMod"),
        (85, "AgeBiggerThanLifetime"),
        (86, "IsTeleporterGroupLinked"),
        (87, "IsCastleBreached"),
        (88, "IsCastleAttacked"),
        (89, "IsInNeutralTeam"),
        (90, "IsOverloaded"),
        (91, "IsInsideOwnerCombatArea"),
        (92, "IsCastleRaided"),
        (93, "IsOwnedByOwner"),
        (94, "HasBuffWithStacks"),
        (95, "IsMinion"),
        (96, "IsCastlePhased"),
        (97, "HasPassive"),
        (98, "GameDifficulty"),
        (99, "BloodQuality"),
        (100, "HasSoulShard"),
        (101, "IsDuringCastlePvPTime"),
        (102, "IsLocalPlayer"),
        (103, "IsInsideOwnerMovementArea"),
        (104, "IsCirclingClockwise"),
        (105, "IsCastleArenaActive"),
        (106, "IsConnectedToArenaFlag"),
        (107, "DistanceBetweenTargetAndOwner"),
        (108, "IsInContest"),
        (109, "IsGalloping"),
        (110, "IsInContestTeam"),
    };
}
