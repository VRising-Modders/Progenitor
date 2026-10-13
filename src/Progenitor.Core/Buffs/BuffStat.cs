using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Buffs;

/// <summary>
/// A stat this API can put on a buff clone. <b>The list is short on purpose.</b> A modifier is on it
/// only when this repository can already say what the component does from its own code, its own
/// live evidence and the game's own apply/destroy systems; a modifier whose numeric meaning nobody
/// here has measured is declared and <em>refused</em> rather than shipped, so the boundary is in
/// the type system and not only in a document.
/// </summary>
public enum BuffStatKind
{
    /// <summary>
    /// Blood drain (<c>ProjectM.ModifyBloodDrainBuff</c>).
    ///
    /// <para><b>Supported.</b> The component is a plain value struct — two floats, three
    /// <c>ModificationId</c>s, two byte-wide <c>ModificationType</c> enums, two priority ints and
    /// three bools, nothing reference-shaped — it is on Core's wire-safe list with a per-field value
    /// policy (<c>PrefabEditWireTypes</c>), and Progenitor's own Sample writes it and has been
    /// observed live stopping a vampire's blood drain. The game owns the reversal: the buff's
    /// spawn system registers the modification through <c>ModificationsRegistry</c> and fills the
    /// <c>ModificationId</c>s on the instance, and the matching destroy system reverses exactly
    /// those ids when the instance dies.</para>
    /// </summary>
    BloodDrain = 0,

    /// <summary>
    /// Movement speed (<c>ProjectM.ModifyMovementSpeedBuff</c>).
    ///
    /// <para><b>Declared and refused.</b> The mechanism is legible from the dump —
    /// <c>Spawn_MoveSpeedBuffSystem.Spawn_Job</c> reads <c>Buff</c> plus this component and
    /// registers a <c>Movement</c> modification, and <c>Destroy_MoveSpeedBuffSystem.Destroy_Job</c>
    /// reverses it through <c>ModifyMovementSpeedBuffModification</c> — and a clone of
    /// <c>Buff_General_Haste</c> visibly carries it. What nobody here has measured is the
    /// <em>value</em>: what <c>MoveSpeed</c> means against the wearer's <c>Movement</c>, what the
    /// <c>MultiplyAdd</c> bool switches between, and above all what <c>CurveReference Curve</c> is —
    /// an asset reference this framework has no verified authoring path for, and which
    /// <c>MoveSpeedBuffSystem.Update_Job</c> resolves against a <c>CurveCollection</c> every tick.
    /// Writing a number into that struct without those three answers is exactly the guess the
    /// project's own rules forbid, so <see cref="BuffStatSupport.Reason"/> names the gap and the
    /// API refuses.</para>
    /// </summary>
    MovementSpeed = 1,
}

/// <summary>How a modifier combines with what is already on the wearer's stat.</summary>
public enum BuffStatOp
{
    /// <summary>Replace the value (<c>ProjectM.ModificationType.Set</c>, 0).</summary>
    Set = 0,

    /// <summary>Add to the value (<c>ProjectM.ModificationType.Add</c>, 3).</summary>
    Add = 1,

    /// <summary>Scale the value (<c>ProjectM.ModificationType.Multiply</c>, 4).</summary>
    Multiply = 2,
}

/// <summary>
/// Which <see cref="BuffStatKind"/> values this build supports, and the exact reason for the ones
/// it does not. Game-free, so the refusal a caller sees and the sentence the docs print are the
/// same string and cannot drift.
/// </summary>
public static class BuffStatSupport
{
    /// <summary>True when <see cref="BuffStats.Apply"/> will write this modifier.</summary>
    public static bool IsSupported(BuffStatKind kind) => kind == BuffStatKind.BloodDrain;

    /// <summary>
    /// Why an unsupported kind is refused — the missing evidence, not a shrug — or an empty string
    /// for a supported one.
    /// </summary>
    public static string Reason(BuffStatKind kind) => kind switch
    {
        BuffStatKind.BloodDrain => "",
        BuffStatKind.MovementSpeed =>
            "movement speed is not a supported modifier yet: ProjectM.ModifyMovementSpeedBuff "
            + "carries a CurveReference that Progenitor has no verified way to author, and the "
            + "meaning of its MoveSpeed value and MultiplyAdd flag has never been measured on a "
            + "live server. The apply and reversal systems are understood "
            + "(Spawn_MoveSpeedBuffSystem / Destroy_MoveSpeedBuffSystem via ModificationsRegistry); "
            + "the numbers are not. See docs/buffs.md 'Supported stat modifiers'.",
        _ => "that stat modifier is not one this build knows about",
    };

    /// <summary>The component this kind lives in, for logs and docs. Never a guess.</summary>
    public static string ComponentName(BuffStatKind kind) => kind switch
    {
        BuffStatKind.BloodDrain => "ProjectM.ModifyBloodDrainBuff",
        BuffStatKind.MovementSpeed => "ProjectM.ModifyMovementSpeedBuff",
        _ => "",
    };

    /// <summary>
    /// The components <see cref="BuffStat.Suppress"/> removes from a plugin's own clone, per kind
    /// (job BUFFP8). Game-free — names only — so the set is testable and a deny-list assertion can
    /// prove nothing lifecycle-shaped ever creeps in.
    ///
    /// <para>The movement pair is the live-proven set: <c>Progenitor.Sample</c> removes exactly
    /// these two for <c>.nodrain</c> (<c>SampleBuffs.cs:3583-3586</c>) and the resulting instance was
    /// observed carrying <c>ModifyBloodDrainBuff</c> and no <c>ModifyMovementSpeedBuff</c>
    /// (internal research notes).</para>
    /// </summary>
    public static IReadOnlyList<string> SuppressionComponents(BuffStatKind kind) => kind switch
    {
        BuffStatKind.MovementSpeed => MovementSuppression,
        _ => Array.Empty<string>(),
    };

    private static readonly string[] MovementSuppression =
    {
        "ModifyMovementSpeedBuff",
        "ModifyMovementSpeedBuffModification",
    };

    /// <summary>
    /// Components a suppression may never remove, whatever the kind. These are attach, lifetime and
    /// sequence machinery: removing any of them is the "blindly strip attached/native lifecycle
    /// components" mistake the conductor prohibits, and DIAG5's standing rules forbid touching the
    /// attach pair at all. Asserted against <see cref="SuppressionComponents"/> by test.
    /// </summary>
    public static readonly string[] NeverSuppressed =
    {
        "Attach",
        "Attached",
        "AttachedBuffer",
        "AttachedDepth",
        "BuffSequenceSpawned",
        "Buff",
        "LifeTime",
        "DestroyTag",
        "EntityOwner",
        "EntityCreator",
    };

    /// <summary>
    /// Whether <see cref="BuffStat.Suppress"/> is allowed for this kind, and why not when it is not.
    ///
    /// <para>Deliberately <b>not</b> the same rule as <see cref="IsSupported"/>. That one is about
    /// authoring a value; this one is about removing a component the base brought along, which needs
    /// no knowledge of the value's semantics. Movement speed is refused for writes and allowed for
    /// suppression, and that inversion is the point.</para>
    /// </summary>
    public static bool MaySuppress(BuffStatKind kind, out string reason)
    {
        if (kind == BuffStatKind.MovementSpeed)
        {
            reason = "";
            return true;
        }

        if (kind == BuffStatKind.BloodDrain)
        {
            reason = "blood drain cannot be suppressed: removing ProjectM.ModifyBloodDrainBuff from "
                + "the clone is not the same thing as setting the wearer's drain to zero, and "
                + "aliasing it to BloodDrain(Set, 0f) would hide which of the two you asked for. "
                + "Use BloodDrain(BuffStatOp.Set, 0f) to stop drain; omit the stat entirely to leave "
                + "the base's drain behavior alone.";
            return false;
        }

        reason = "that stat modifier is not one this build knows how to suppress";
        return false;
    }

    /// <summary>The refusal line. Grep <c>buff stat refused</c>.</summary>
    public static string RefusedLine(string buffId, BuffStatKind kind) =>
        "buff stat refused id='" + (buffId ?? "") + "' stat=" + kind + ": " + Reason(kind);

    /// <summary>
    /// Whether a definition may carry this plugin's stat modifiers, and the sentence to print when
    /// it may not. Game-free on purpose: the decision is an id match, a kind comparison and an owner
    /// check, none of which needs a game, and keeping it here is what lets the suite drive the real
    /// refusals instead of a copy of them. The live half (<c>BuffStats</c>) supplies the three
    /// facts and does the writing.
    /// </summary>
    /// <param name="found">A definition with this id is in the registry.</param>
    /// <param name="isBuff">That definition is a buff (not an item or a recipe).</param>
    /// <param name="ownedByCaller">The registry recorded the calling plugin as its owner.</param>
    public static bool MayCarryStats(
        string? buffId, bool found, bool isBuff, bool ownedByCaller, string? kindName, out string reason)
    {
        string id = buffId ?? "";
        if (!found)
        {
            reason = "no buff definition with that id is registered; call "
                + "ctx.Content.DefineBuff(\"" + id + "\").CloneOf(...).Register() first";
            return false;
        }

        if (!isBuff)
        {
            reason = "'" + id + "' is registered as "
                + (string.IsNullOrEmpty(kindName) ? "something else" : kindName)
                + ", not a buff; stat modifiers go on a DefineBuff definition";
            return false;
        }

        if (!ownedByCaller)
        {
            reason = "'" + id + "' belongs to another plugin; a plugin may only put stat modifiers "
                + "on a buff clone it registered itself";
            return false;
        }

        reason = "";
        return true;
    }

    /// <summary>The line a refused <c>Stats</c> call writes. Grep <c>buff stats refused</c>.</summary>
    public static string StatsRefusedLine(string? buffId, string reason) =>
        "buff stats refused id='" + (buffId ?? "") + "': " + reason;

    /// <summary>The line a <c>Stats</c> call with nothing supported to write writes.</summary>
    public static string NothingToWriteLine(string? buffId) =>
        "buff stats id='" + (buffId ?? "")
        + "' has nothing supported to write; no edit registered";
}

/// <summary>
/// One stat modifier to put on a buff clone. Built through the named factory so an unsupported
/// combination is a value you can inspect and log, not an exception thrown from a builder.
/// </summary>
/// <summary>
/// Which of a blood-drain buff's two drain values a stat writes (job BUFFP5).
///
/// <para><b>This exists because writing both silently registers two modifications.</b> Live on
/// 2026-09-13 a single <c>BloodDrain(Multiply, 0.1f)</c> produced two modification ids against the
/// same modifiable value — <c>ProjectM.Blood+4</c> — and the wearer's drain went
/// <c>0.005 -> 5e-5</c>, i.e. <c>0.1 x 0.1 = 0.01x</c>, not the 0.1x the caller asked for. The
/// defect was invisible until then because every earlier use was <c>Set 0</c>, where writing the
/// same value twice is idempotent.</para>
/// </summary>
public enum BloodDrainScope
{
    /// <summary>
    /// Write the active drain only: one modification, so <c>Multiply(0.1)</c> means 0.1x. The idle
    /// value, type and priority are left exactly as read and <c>AffectIdleBloodValue</c> is cleared.
    /// <b>The default</b>, because one stat meaning one modification is the contract a caller
    /// reasonably expects.
    /// </summary>
    Active = 0,

    /// <summary>
    /// Write both the active and the idle drain. <b>Registers two modifications against the same
    /// value.</b> Harmless under <see cref="BuffStatOp.Set"/> (idempotent) and compounding under
    /// <see cref="BuffStatOp.Add"/> / <see cref="BuffStatOp.Multiply"/>. Ask for it deliberately
    /// when you want idle drain affected too, and prefer <c>Set</c> with it.
    /// </summary>
    ActiveAndIdle = 1,
}

public sealed class BuffStat
{
    private BuffStat(BuffStatKind kind, BuffStatOp op, float value, int priority,
                     BloodDrainScope scope, bool suppress)
    {
        Kind = kind;
        Op = op;
        Value = value;
        Priority = priority;
        Scope = scope;
        IsSuppression = suppress;
    }

    /// <summary>Which stat.</summary>
    public BuffStatKind Kind { get; }

    /// <summary>How the value combines. Maps onto <c>ProjectM.ModificationType</c>.</summary>
    public BuffStatOp Op { get; }

    /// <summary>The number. Must be finite; what it means is the stat's own business.</summary>
    public float Value { get; }

    /// <summary>
    /// Sort key among competing modifiers of the same stat. It is compared, never indexed with, so
    /// any int is in domain; a higher one wins a <see cref="BuffStatOp.Set"/> race.
    /// <b>It does not make this application own another source's contribution</b> — each buff
    /// instance still registers and reverses its own modification.
    /// </summary>
    public int Priority { get; }

    /// <summary>
    /// Which drain values a <see cref="BuffStatKind.BloodDrain"/> stat writes. Meaningless for other
    /// kinds and for a suppression.
    /// </summary>
    public BloodDrainScope Scope { get; }

    /// <summary>
    /// True when this is a <see cref="Suppress"/> request: remove what the cloned base carries for
    /// this kind, rather than write a value. <see cref="Op"/>, <see cref="Value"/> and
    /// <see cref="Priority"/> carry no meaning.
    /// </summary>
    public bool IsSuppression { get; }

    /// <summary>
    /// Blood drain. <c>BloodDrain(BuffStatOp.Set, 0f)</c> is "this wearer does not drain";
    /// <c>BloodDrain(BuffStatOp.Multiply, 0.5f)</c> halves it.
    ///
    /// <para><paramref name="scope"/> defaults to <see cref="BloodDrainScope.Active"/>: one
    /// modification, so the number means what it says. Pass
    /// <see cref="BloodDrainScope.ActiveAndIdle"/> to affect idle drain too, knowing it registers
    /// two modifications against the same value — see that enum for the live evidence.</para>
    /// </summary>
    public static BuffStat BloodDrain(BuffStatOp op, float value, int priority = 0,
                                      BloodDrainScope scope = BloodDrainScope.Active) =>
        new(BuffStatKind.BloodDrain, op, value, priority, scope, suppress: false);

    /// <summary>
    /// Any declared kind, including one this build refuses. Useful for a caller that reads a stat
    /// name from configuration and wants the refusal rather than a missing branch.
    /// </summary>
    public static BuffStat Of(BuffStatKind kind, BuffStatOp op, float value, int priority = 0,
                              BloodDrainScope scope = BloodDrainScope.Active) =>
        new(kind, op, value, priority, scope, suppress: false);

    /// <summary>
    /// Remove what the cloned vanilla base carries for <paramref name="kind"/>, so the buff does not
    /// grant an effect you never asked for (job BUFFP8).
    ///
    /// <para>A clone inherits <b>everything</b> from its base. <c>Buff_General_Haste</c> is the
    /// documented choice for a visible timed buff because of <c>ShowOnBuffBar</c>, <c>LifeTime</c>
    /// and <c>EndAction</c> — and it also carries <c>ModifyMovementSpeedBuff</c>. Live on
    /// 2026-09-13 both worked examples granted +50% run speed nobody had asked for
    /// (<c>ProjectM.Movement+92</c> patched 6 -> 4 on removal).</para>
    ///
    /// <para><b>Suppression is not the same operation as writing a value</b>, and that distinction
    /// is why this is supported for movement speed while
    /// <c>BloodDrain</c>-style <i>writes</i> to movement speed stay refused: removing a component
    /// needs none of the three unknowns (what <c>MoveSpeed</c> means against the wearer's
    /// <c>Movement</c>, what <c>MultiplyAdd</c> switches between, what <c>CurveReference Curve</c>
    /// resolves to) that make authoring a movement value a guess.</para>
    ///
    /// <para><c>Suppress(BloodDrain)</c> is <b>refused</b>: it is not an alias for
    /// <c>BloodDrain(Set, 0)</c> and must not silently become one.</para>
    /// </summary>
    public static BuffStat Suppress(BuffStatKind kind) =>
        new(kind, BuffStatOp.Set, 0f, 0, BloodDrainScope.Active, suppress: true);

    /// <summary>True when this modifier can be written: a supported kind and a finite value.</summary>
    public bool IsUsable(out string reason)
    {
        if (IsSuppression)
        {
            // Suppression asks a different question from a write, so it has its own support rule:
            // BuffStatSupport.IsSupported is about authoring a VALUE. Movement speed can be removed
            // safely and cannot be written safely, which is exactly the inversion of the write rule.
            if (!BuffStatSupport.MaySuppress(Kind, out string why))
            {
                reason = why;
                return false;
            }

            reason = "";
            return true;
        }

        if (!BuffStatSupport.IsSupported(Kind))
        {
            reason = BuffStatSupport.Reason(Kind);
            return false;
        }

        if (float.IsNaN(Value) || float.IsInfinity(Value))
        {
            reason = "the value must be a finite number; NaN or an infinity propagates into every "
                + "total the game computes from this stat";
            return false;
        }

        reason = "";
        return true;
    }

    public override string ToString() =>
        IsSuppression
            ? "Suppress(" + Kind + ")"
            : Kind + "(" + Op + " " + Value.ToString(CultureInfo.InvariantCulture)
              + (Priority == 0 ? "" : ", priority " + Priority.ToString(CultureInfo.InvariantCulture))
              + (Kind == BuffStatKind.BloodDrain && Scope == BloodDrainScope.ActiveAndIdle
                  ? ", active+idle (two modifications)" : "")
              + ")";
}
