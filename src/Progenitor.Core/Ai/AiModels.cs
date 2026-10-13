using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Ai;

// Game-free public shapes of ctx.Ai. Compiled into the test assembly, so nothing here may name
// a Unity, ProjectM or Stunlock type. Values of the three cast-option enums are the game's own
// (server metadata); AiCodecTests pins them against the generated Layout.

/// <summary>Which candidates a cast-option row may target (<c>CastOptionAggroFilter</c>).</summary>
public enum CastAggroFilter
{
    /// <summary>The candidate must be on the boss's aggro list.</summary>
    InAggroListOnly = 0,

    /// <summary>The candidate must be the boss's primary aggro target.</summary>
    PrimaryAggroTargetOnly = 1,

    /// <summary>Any candidate that passes the row's other checks.</summary>
    None = Layout.CastOptionAggroFilterNone,
}

/// <summary>Line-of-sight rule of a cast-option row (<c>CastOptionLineOfSightRequirement</c>).</summary>
public enum CastLineOfSight
{
    /// <summary>The boss must see the target.</summary>
    RequireLineOfSight = 0,

    /// <summary>The boss must not see the target.</summary>
    RequireNoLineOfSight = 1,

    /// <summary>No line-of-sight test.</summary>
    None = Layout.CastOptionLineOfSightNone,
}

/// <summary>Facing rule of a cast-option row (<c>CastOptionAngleRequirement</c>).</summary>
public enum CastAngle
{
    None = 0,
    LessThan90 = 1,
    LessThan45 = 2,
    LessThan10 = 3,
    GreaterThan135 = 4,
    GreaterThan90 = 5,
    GreaterThan45 = 6,
    Clockwise90 = 7,
    CounterClock90 = 8,
    ClockSide90 = 9,
    CounterClockSide90 = 10,
    ClockSide180 = 11,
    CounterClockSide180 = Layout.CastOptionAngleCounterClockSide180,
}

/// <summary>
/// Literal node parameters <see cref="TreeVariant.SetParameter"/> may write. Each one names the
/// node type it belongs to; the write is refused on any other node type. Register fields are never
/// writable: they are byte offsets into the unit's blackboard, not values.
/// </summary>
public enum TreeParameter
{
    /// <summary><c>Delay.MinTime</c>, seconds.</summary>
    DelayMinTime,

    /// <summary><c>Delay.MaxTime</c>, seconds.</summary>
    DelayMaxTime,

    /// <summary><c>HealthChangePercentage.Amount</c>, a fraction of max health.</summary>
    HealthChangePercentageAmount,

    /// <summary><c>SetCombatAreaRelocationDistance.Value</c>, meters.</summary>
    CombatAreaRelocationDistance,

    /// <summary><c>Int_Set.Value</c> (written as an integer).</summary>
    IntSetValue,
}

/// <summary>
/// Field changes for the cast-option rows of one ability group. Null leaves a field alone.
/// <see cref="CooldownSeconds"/> always writes an exact cooldown (the runtime reads
/// <c>Min &lt; 0</c> as "exactly Max seconds"); the ability's own cooldown still applies as a floor.
/// </summary>
public sealed class CastOptionEdit
{
    /// <summary>Integer weight inside the row's priority group. 0 disables the row.</summary>
    public int? Weight { get; set; }

    /// <summary>Exact row cooldown in seconds, 0 or more.</summary>
    public float? CooldownSeconds { get; set; }

    /// <summary>Minimum target distance in meters.</summary>
    public float? RangeMin { get; set; }

    /// <summary>Maximum target distance in meters.</summary>
    public float? RangeMax { get; set; }

    /// <summary>Line-of-sight rule.</summary>
    public CastLineOfSight? LineOfSight { get; set; }

    /// <summary>Which candidates may be targeted.</summary>
    public CastAggroFilter? AggroFilter { get; set; }

    /// <summary>Facing rule.</summary>
    public CastAngle? Angle { get; set; }

    /// <summary>True when no field is set.</summary>
    public bool IsEmpty =>
        Weight == null && CooldownSeconds == null && RangeMin == null && RangeMax == null
        && LineOfSight == null && AggroFilter == null && Angle == null;

    /// <summary>Throws <see cref="ArgumentException"/> for a negative or non-finite value.</summary>
    public void Validate()
    {
        if (IsEmpty) throw new ArgumentException("CastOptionEdit sets no field.");
        if (Weight is < 0) throw new ArgumentException("Weight must be 0 or more.");
        CheckFinite(CooldownSeconds, nameof(CooldownSeconds));
        CheckFinite(RangeMin, nameof(RangeMin));
        CheckFinite(RangeMax, nameof(RangeMax));
        if (RangeMin != null && RangeMax != null && RangeMin > RangeMax)
            throw new ArgumentException("RangeMin must not exceed RangeMax.");
        if (LineOfSight != null && !Enum.IsDefined(LineOfSight.Value)) throw new ArgumentException("Unknown LineOfSight.");
        if (AggroFilter != null && !Enum.IsDefined(AggroFilter.Value)) throw new ArgumentException("Unknown AggroFilter.");
        if (Angle != null && !Enum.IsDefined(Angle.Value)) throw new ArgumentException("Unknown Angle.");
    }

    private static void CheckFinite(float? value, string name)
    {
        if (value == null) return;
        if (float.IsNaN(value.Value) || float.IsInfinity(value.Value) || value.Value < 0f)
            throw new ArgumentException(name + " must be a finite number, 0 or more.");
    }
}

/// <summary>One slot of the boss's <c>AbilityGroupSlotBuffer</c>. Cast-option rows cast by slot.</summary>
public sealed class AiAbilitySlot
{
    public AiAbilitySlot(int slot, int groupGuid, string name)
    {
        Slot = slot;
        GroupGuid = groupGuid;
        Name = name ?? "";
    }

    public int Slot { get; }
    public int GroupGuid { get; }
    public string Name { get; }
}

/// <summary>One decoded <c>CastOptionBuffer</c> row.</summary>
public sealed class AiCastOptionRow
{
    /// <summary>Priority group (0 is tried first; the first group that produces a cast wins).</summary>
    public int PriorityGroup { get; internal set; }

    /// <summary>Position inside the group. Informational: the public API never selects by it.</summary>
    internal int RowIndex { get; set; }

    public int AbilitySlot { get; internal set; }
    public int AbilityGroupGuid { get; internal set; }
    public string AbilityGroupName { get; internal set; } = "";
    public int Weight { get; internal set; }
    public float RangeMin { get; internal set; }
    public float RangeMax { get; internal set; }

    /// <summary>Raw runtime cooldown pair. <c>Min &lt; 0</c> means exactly <see cref="CooldownMax"/>.</summary>
    public float CooldownMin { get; internal set; }

    public float CooldownMax { get; internal set; }
    public CastAggroFilter AggroFilter { get; internal set; }
    public CastAngle Angle { get; internal set; }
    public int IgnoreCooldownFlags { get; internal set; }
    public bool InterruptOnTargetDeath { get; internal set; }
    public CastLineOfSight LineOfSight { get; internal set; }
    public float SuccessDelayMin { get; internal set; }
    public float SuccessDelayMax { get; internal set; }
    public int TieBreakerSource { get; internal set; }
    public int TieBreakerPick { get; internal set; }
    public float ComboIndex { get; internal set; }
    public float ComboResetTime { get; internal set; }
    public bool HasCondition { get; internal set; }
    public bool HasGroupCondition { get; internal set; }

    /// <summary>Readable summary of the row condition, not the whole graph. Empty without one.</summary>
    public string ConditionSummary { get; internal set; } = "";

    /// <summary>The boss health fraction the row condition requires (<c>HealthFactor</c>), if any.</summary>
    public float? HealthGate { get; internal set; }

    /// <summary>"exactly 25 s" or "random 2 to 4 s".</summary>
    public string CooldownText =>
        CooldownMin < 0f
            ? "exactly " + F(CooldownMax) + " s"
            : "random " + F(CooldownMin) + " to " + F(CooldownMax) + " s";

    public override string ToString() =>
        "group " + PriorityGroup + " slot " + AbilitySlot + " " + (AbilityGroupName.Length > 0 ? AbilityGroupName : AbilityGroupGuid.ToString(CultureInfo.InvariantCulture))
        + " weight " + Weight + " range " + F(RangeMin) + ".." + F(RangeMax) + " cooldown " + CooldownText
        + " aggro " + AggroFilter + " los " + LineOfSight
        + (HealthGate is float g ? " hp<=" + F(g) : "")
        + (ConditionSummary.Length > 0 ? " if " + ConditionSummary : "");

    internal static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>One priority group of cast-option rows for one behavior-tree state mask.</summary>
public sealed class AiCastOptionGroup
{
    public AiCastOptionGroup(int stateMask, int priority, IReadOnlyList<AiCastOptionRow> rows)
    {
        StateMask = stateMask;
        Priority = priority;
        Rows = rows ?? Array.Empty<AiCastOptionRow>();
    }

    /// <summary><c>GenericEnemyState</c> flags the group runs in (Frostmaw: Combat | Relocate_Unstuck).</summary>
    public int StateMask { get; }

    public int Priority { get; }
    public IReadOnlyList<AiCastOptionRow> Rows { get; }
}

/// <summary>A boss's <c>Script_ApplyBuffUnderHealthThreshold</c> phase script.</summary>
public sealed class AiPhaseScript
{
    public AiPhaseScript(float healthFactor, int newBuffGuid, string newBuffName, bool thresholdMet)
    {
        HealthFactor = healthFactor;
        NewBuffGuid = newBuffGuid;
        NewBuffName = newBuffName ?? "";
        ThresholdMet = thresholdMet;
    }

    /// <summary>The buff is applied on the first damaging hit that leaves health at or below this fraction.</summary>
    public float HealthFactor { get; }

    public int NewBuffGuid { get; }
    public string NewBuffName { get; }

    /// <summary>True once the buff fired. Re-arms only after a full heal.</summary>
    public bool ThresholdMet { get; }

    public override string ToString() =>
        "apply " + (NewBuffName.Length > 0 ? NewBuffName : NewBuffGuid.ToString(CultureInfo.InvariantCulture))
        + " at hp<=" + AiCastOptionRow.F(HealthFactor) + (ThresholdMet ? " (fired)" : "");
}

/// <summary>A faction prefab's <c>FactionBlobAsset</c>: its index and relationship masks.</summary>
public sealed class AiFactionInfo
{
    public AiFactionInfo(string name, int guid, int index, int membership, int friendly, int neutral, int hostile)
    {
        Name = name ?? "";
        Guid = guid;
        Index = index;
        Membership = membership;
        Friendly = friendly;
        Neutral = neutral;
        Hostile = hostile;
    }

    public string Name { get; }
    public int Guid { get; }

    /// <summary>The runtime faction index units carry in <c>Team.FactionIndex</c>.</summary>
    public int Index { get; }

    public int Membership { get; }
    public int Friendly { get; }
    public int Neutral { get; }
    public int Hostile { get; }

    /// <summary>
    /// True when two units of this faction are allies (<c>Friendly &amp; Membership</c> is not 0).
    /// Faction_Wendigo is not: several Frostmaws on it damage and down each other.
    /// </summary>
    public bool SelfFriendly => (Friendly & Membership) != 0;

    public override string ToString() =>
        Name + " index=" + Index + " membership=" + Membership + " friendly=" + Friendly
        + " hostile=" + Hostile + (SelfFriendly ? " self-friendly" : " NOT self-friendly");
}

/// <summary>One <c>CastHistoryBufferElement</c>, newest first.</summary>
public sealed class AiCastHistoryEntry
{
    public AiCastHistoryEntry(int abilityGroupGuid, string name, double castTime, bool interrupted)
    {
        AbilityGroupGuid = abilityGroupGuid;
        Name = name ?? "";
        CastTime = castTime;
        Interrupted = interrupted;
    }

    public int AbilityGroupGuid { get; }
    public string Name { get; }

    /// <summary>Server time of the cast, seconds.</summary>
    public double CastTime { get; }

    public bool Interrupted { get; }
}

/// <summary>A boss prefab's AI as <see cref="PluginAi.Describe"/> reads it.</summary>
public sealed class AiProfile
{
    public string BossName { get; internal set; } = "";
    public int BossGuid { get; internal set; }
    public string TreeName { get; internal set; } = "";
    public int TreeGuid { get; internal set; }
    public int TreeNodeCount { get; internal set; }
    public int BlackboardSize { get; internal set; }

    /// <summary>Node type name to count, most frequent first.</summary>
    public IReadOnlyList<KeyValuePair<string, int>> NodeTypes { get; internal set; } = Array.Empty<KeyValuePair<string, int>>();

    public string CastOptionsName { get; internal set; } = "";
    public int CastOptionsGuid { get; internal set; }
    public IReadOnlyList<AiAbilitySlot> Slots { get; internal set; } = Array.Empty<AiAbilitySlot>();
    public IReadOnlyList<AiCastOptionGroup> Groups { get; internal set; } = Array.Empty<AiCastOptionGroup>();

    /// <summary>The supported phase script, or null when the prefab has none.</summary>
    public AiPhaseScript? Phase { get; internal set; }

    /// <summary>Other health-threshold scripts the prefab carries that ctx.Ai cannot edit yet.</summary>
    public IReadOnlyList<string> UnsupportedPhaseScripts { get; internal set; } = Array.Empty<string>();

    /// <summary><c>Team.Value</c> (2 = unit team).</summary>
    public int TeamValue { get; internal set; }

    /// <summary><c>Team.FactionIndex</c> as the prefab ships it.</summary>
    public int TeamFactionIndex { get; internal set; }

    /// <summary>The faction <c>FactionReference</c> names, when it resolves.</summary>
    public AiFactionInfo? Faction { get; internal set; }

    /// <summary>The prefab's <c>Immortal</c> flag. True for spawned V Bloods: they are downed at 0 HP, not killed.</summary>
    public bool Immortal { get; internal set; }

    /// <summary>Multi-line text for a log or a chat reply.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(BossName).Append(" (").Append(BossGuid.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        sb.Append("tree ").Append(TreeName).Append(' ').Append(TreeNodeCount).Append(" nodes, blackboard ").Append(BlackboardSize).Append(" bytes\n");
        sb.Append("cast options ").Append(CastOptionsName).Append(": ").Append(Groups.Count).Append(" group(s)\n");
        foreach (var group in Groups)
            foreach (var row in group.Rows)
                sb.Append("  ").Append(row).Append('\n');
        sb.Append("phase ").Append(Phase?.ToString() ?? "none");
        if (UnsupportedPhaseScripts.Count > 0) sb.Append(" (unsupported: ").Append(string.Join(", ", UnsupportedPhaseScripts)).Append(')');
        sb.Append('\n');
        sb.Append("team ").Append(TeamValue).Append('/').Append(TeamFactionIndex).Append(", faction ").Append(Faction?.ToString() ?? "unknown");
        sb.Append(", immortal ").Append(Immortal ? "yes" : "no");
        return sb.ToString();
    }
}

/// <summary>What one live unit's AI is doing now (<see cref="PluginAi.Observe"/>).</summary>
public sealed class AiObservation
{
    public string Unit { get; internal set; } = "";
    public string Prefab { get; internal set; } = "";

    /// <summary><c>BehaviourTreeState</c> as flag names, for example <c>Combat</c>.</summary>
    public string State { get; internal set; } = "";

    public int StateValue { get; internal set; }

    /// <summary>Primary aggro target as <c>index:version</c>, empty when none.</summary>
    public string Target { get; internal set; } = "";

    /// <summary>Ability group being cast, empty when idle.</summary>
    public string CurrentCast { get; internal set; } = "";

    public float Health { get; internal set; }
    public float MaxHealth { get; internal set; }
    public bool IsDead { get; internal set; }
    public int TeamValue { get; internal set; }
    public int FactionIndex { get; internal set; }

    /// <summary>Tree the unit runs now, and whether it is a private <see cref="TreeVariant"/> copy.</summary>
    public string Tree { get; internal set; } = "";

    public bool TreeIsVariant { get; internal set; }
    public int CastOptionsGuid { get; internal set; }
    public string CastOptions { get; internal set; } = "";

    /// <summary>The <see cref="PluginAi.Variants"/> variant applied to the unit, if any.</summary>
    public string Variant { get; internal set; } = "";

    public AiPhaseScript? Phase { get; internal set; }
    public IReadOnlyList<AiCastHistoryEntry> History { get; internal set; } = Array.Empty<AiCastHistoryEntry>();

    public override string ToString()
    {
        var sb = new StringBuilder();
        sb.Append(Unit).Append(' ').Append(Prefab);
        if (Variant.Length > 0) sb.Append(" [").Append(Variant).Append(']');
        sb.Append(" state=").Append(State.Length > 0 ? State : StateValue.ToString(CultureInfo.InvariantCulture));
        sb.Append(" hp=").Append(AiCastOptionRow.F(Health)).Append('/').Append(AiCastOptionRow.F(MaxHealth));
        if (Target.Length > 0) sb.Append(" target=").Append(Target);
        if (CurrentCast.Length > 0) sb.Append(" casting=").Append(CurrentCast);
        sb.Append(" team=").Append(TeamValue).Append('/').Append(FactionIndex);
        if (TreeIsVariant) sb.Append(" tree=variant");
        if (History.Count > 0)
        {
            sb.Append(" recent=");
            for (int i = 0; i < History.Count && i < 4; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append(History[i].Name.Length > 0 ? AiNames.Short(History[i].Name) : History[i].AbilityGroupGuid.ToString(CultureInfo.InvariantCulture));
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// A named per-unit AI variant for <see cref="PluginAi.Variants"/>: tree edits, per-instance
/// cast-option edits, a phase threshold and a faction. Built once, applied to many units.
/// </summary>
public sealed class VariantSpec
{
    internal readonly List<KeyValuePair<string, CastOptionEdit>> CastEdits = new();
    internal readonly List<KeyValuePair<string, float>> HealthGates = new();

    /// <summary>Boss prefab (spawnable name or signed GUID hash) the variant applies to.</summary>
    public string BossPrefab { get; }

    public VariantSpec(string bossPrefab)
    {
        if (string.IsNullOrWhiteSpace(bossPrefab)) throw new ArgumentException("Boss prefab is required.", nameof(bossPrefab));
        BossPrefab = bossPrefab.Trim();
    }

    /// <summary>Edits made to the variant's private tree copy before it is first applied. Null keeps the prefab tree.</summary>
    public Action<TreeVariant>? Tree { get; set; }

    /// <summary>
    /// Boss health fraction for the phase script (<c>Script_ApplyBuffUnderHealthThreshold</c>).
    /// Null keeps the unit's own value.
    /// </summary>
    public float? PhaseThreshold { get; set; }

    /// <summary>Faction prefab name or GUID written to the unit's <c>FactionReference</c> and <c>Team.FactionIndex</c>. Null keeps both.</summary>
    public string? Faction { get; set; }

    /// <summary>
    /// Required for <see cref="Tree"/>, <see cref="EditCastOption"/> and <see cref="HealthGate"/>.
    /// Both paths are experimental until live-verified across a save (docs/ai.md): a private tree
    /// reference is not saved (the unit reloads with a null tree, which Core repairs to vanilla), and
    /// a cast-option clone GUID in a saved unit is unproven. Revert or remove variant units before
    /// saving.
    /// </summary>
    public bool AllowExperimental { get; set; }

    /// <summary>
    /// Vanilla buff the variant's persistence marker is cloned from. Only sources Core can make inert
    /// are accepted (today <c>Buff_General_Haste</c>); Define refuses any other.
    /// </summary>
    public string MarkerSource { get; set; } = "Buff_General_Haste";

    /// <summary>Change the rows of <paramref name="abilityGroup"/> in this variant's private cast options.</summary>
    public VariantSpec EditCastOption(string abilityGroup, CastOptionEdit edit)
    {
        if (string.IsNullOrWhiteSpace(abilityGroup)) throw new ArgumentException("Ability group is required.", nameof(abilityGroup));
        if (edit == null) throw new ArgumentNullException(nameof(edit));
        edit.Validate();
        CastEdits.Add(new KeyValuePair<string, CastOptionEdit>(abilityGroup.Trim(), edit));
        return this;
    }

    /// <summary>Move the health gate of <paramref name="abilityGroup"/>'s rows to <paramref name="fraction"/>.</summary>
    public VariantSpec HealthGate(string abilityGroup, float fraction)
    {
        if (string.IsNullOrWhiteSpace(abilityGroup)) throw new ArgumentException("Ability group is required.", nameof(abilityGroup));
        AiChecks.Fraction(fraction, nameof(fraction));
        HealthGates.Add(new KeyValuePair<string, float>(abilityGroup.Trim(), fraction));
        return this;
    }

    internal bool UsesCastOptions => CastEdits.Count > 0 || HealthGates.Count > 0;
}

internal static class AiChecks
{
    internal static void Fraction(float value, string name)
    {
        if (float.IsNaN(value) || value < 0f || value > 1f)
            throw new ArgumentOutOfRangeException(name, "A health fraction is from 0 to 1.");
    }
}
