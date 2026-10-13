using System;
using System.Globalization;

namespace Progenitor.Abilities;

/// <summary>
/// The game-free half of <see cref="AbilityCast"/>: every refusal reason and every log line, so
/// the decisions can be driven by the test suite without a world. The interop half holds no text.
///
/// <para><b>What the mechanism is.</b> <c>AbilityBar_Shared.ForceCastGroup</c> is the game's own
/// "cast this group now" inbox on a character's ability bar. The server's cast pipeline reads it,
/// starts one cast, and writes the field back to <c>0</c>. That consume-and-clear is the whole
/// reason a single <c>int</c> write is a complete trigger rather than a latch a mod has to
/// maintain: nothing has to clear it afterwards and it cannot repeat on its own.</para>
///
/// <para><b>Measured, not assumed</b> (2026-09-06, test server, character with no input): writing
/// the field advanced <c>CastStartedCounter</c> by exactly 1 and <c>ServerCastCounter</c> by
/// exactly 1, and the field read back <c>0</c>. Repeated with the group on <b>no</b> bar slot and
/// with every slot but two empty: same result. <b>Not reproduced on 2026-09-25:</b> an off-bar
/// <c>AB_Unholy_CorruptedSkull_AbilityGroup</c> on a player was consumed and never started, while
/// <c>ServerGameManager.ForceCastAbilityGroup</c> (<see cref="AbilityCast.ForceCastGroup"/>) did
/// start it. Assume this field needs the group on the bar.</para>
///
/// <para><b>Why the pending check refuses instead of overwriting.</b> A non-zero
/// <c>ForceCastGroup</c> means the pipeline has not consumed the previous request yet. Overwriting
/// it would silently drop somebody's cast — including the game's own, since this is a vanilla
/// field and Progenitor is not its only writer. Refusing keeps the failure visible and costs the
/// caller one frame.</para>
/// </summary>
public static class AbilityCastPolicy
{
    /// <summary>The value the game leaves in the field when nothing is pending.</summary>
    public const int NoPendingCast = 0;

    /// <summary>
    /// A request is only worth writing when the caller named a real group. <c>0</c> is
    /// <c>PrefabGUID.Empty</c>, which is also the field's idle value, so writing it would be a
    /// no-op the caller could not tell from a cast.
    /// </summary>
    public static bool IsCastableGroup(int groupHash) => groupHash != NoPendingCast;

    /// <summary>True when the pipeline still owes a cast for a previous write.</summary>
    public static bool HasPendingCast(int forceCastGroupValue) => forceCastGroupValue != NoPendingCast;

    public static string EmptyGroupRefusal() =>
        "ability cast refused: group is PrefabGUID.Empty, which is the field's own idle value; "
        + "name the ability group to cast.";

    public static string NoWorldRefusal() =>
        "ability cast refused: no server world. ForceCastGroup is read by the server's cast "
        + "pipeline, so this is a server-side call.";

    public static string DeadCharacterRefusal(int index, int version) =>
        "ability cast refused: character "
        + index.ToString(CultureInfo.InvariantCulture) + ":" + version.ToString(CultureInfo.InvariantCulture)
        + " does not exist.";

    public static string NoAbilityBarRefusal(int index, int version) =>
        "ability cast refused: character "
        + index.ToString(CultureInfo.InvariantCulture) + ":" + version.ToString(CultureInfo.InvariantCulture)
        + " has no AbilityBar_Shared, so it has no cast pipeline to drive.";

    public static string PendingRefusal(int pendingGroup) =>
        "ability cast refused: ForceCastGroup already holds "
        + pendingGroup.ToString(CultureInfo.InvariantCulture)
        + " and the pipeline has not consumed it yet. Overwriting would drop that cast; try again next frame.";

    public static string RawReadRefusal(int index, int version) =>
        "ability cast refused: GetComponentDataRawRW returned null for character "
        + index.ToString(CultureInfo.InvariantCulture) + ":" + version.ToString(CultureInfo.InvariantCulture) + ".";

    /// <summary>The one line a successful forced cast writes.</summary>
    public static string FormatCast(int groupHash, int index, int version, int castStartedBefore) =>
        "Abilities: forced cast of group " + groupHash.ToString(CultureInfo.InvariantCulture)
        + " on " + index.ToString(CultureInfo.InvariantCulture) + ":" + version.ToString(CultureInfo.InvariantCulture)
        + " (castStartedCounter was " + castStartedBefore.ToString(CultureInfo.InvariantCulture)
        + "; the pipeline clears ForceCastGroup when it takes it).";
}
