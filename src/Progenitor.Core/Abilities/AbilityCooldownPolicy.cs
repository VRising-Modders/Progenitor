using System;
using System.Collections.Generic;
using System.Globalization;

namespace Progenitor.Abilities;

/// <summary>
/// The game-free half of <c>ctx.Abilities.NoCooldowns</c> and <c>ctx.Abilities.ForceCast</c>:
/// the timing gates, the "does this field need a write" decisions, the per-character hold
/// bookkeeping and every refusal / log line. The interop half (<c>NoCooldownsRuntime</c>) only
/// walks entities and pokes the fields these methods say to poke.
///
/// <para><b>What "no cooldowns" writes</b> (server dump; offsets in <c>layout.txt</c>):
/// <c>AbilityCooldownState.CooldownEndTime</c> (double @0x0) and <c>CurrentCooldown</c> (float
/// @0x8) to 0 on every ability entity whose <c>EntityOwner.Owner</c> is the character, and
/// <c>AbilityChargesState.CurrentCharges</c> (int @0x4) up to <c>AbilityChargesData.MaxCharges</c>
/// (int @0x8) on every owned entity that carries charges. Cast times, animations and the charge
/// recharge timer (<c>AbilityChargesState.ChargeTime</c>) are never touched.</para>
/// </summary>
public static class AbilityCooldownPolicy
{
    /// <summary>How often the cooldown fields of a held character are zeroed.</summary>
    public const double ZeroIntervalSeconds = 0.1;

    /// <summary>
    /// After a rebuild triggered by a cast, one more rebuild this long afterwards, in case the
    /// ability entity the cast created was not there yet when the counter was seen to move.
    /// </summary>
    public const double FollowUpRebuildSeconds = 0.5;

    /// <summary>The "never ran" timestamp for <see cref="Due"/>.</summary>
    public const long Never = long.MinValue;

    /// <summary>
    /// <c>AbilityChargesState.CurrentCharges</c> is synced as <c>SyncRangedInt(0, 255)</c>
    /// (server dump); a larger value would not survive replication, so a write never exceeds it.
    /// </summary>
    public const int MaxSyncedCharges = 255;

    /// <summary>
    /// True when at least <paramref name="intervalSeconds"/> have passed between
    /// <paramref name="lastMs"/> and <paramref name="nowMs"/> (both <c>Environment.TickCount64</c>),
    /// or when it never ran. A clock that went backwards counts as due, so a wrapped or reset
    /// timestamp cannot stall the loop.
    /// </summary>
    public static bool Due(long nowMs, long lastMs, double intervalSeconds)
    {
        if (lastMs == Never) return true;
        long elapsed = nowMs - lastMs;
        if (elapsed < 0) return true;
        return elapsed >= (long)Math.Round(intervalSeconds * 1000.0);
    }

    /// <summary>
    /// True when a held character's <c>AbilityBar_Shared.CastStartedCounter</c> moved since the last
    /// look: a cast started, so a new ability entity may exist. An unreadable counter (-1) is not
    /// an advance.
    /// </summary>
    public static bool CounterAdvanced(int lastSeen, int current) => current >= 0 && current != lastSeen;

    /// <summary>
    /// Whether the owned-entity lists must be rebuilt this tick: never built yet (a new hold), a
    /// held character's cast counter advanced, or the follow-up after such a rebuild is due.
    /// There is no fixed timer: while nobody casts, nothing scans the world.
    /// </summary>
    public static bool NeedsRebuild(bool neverBuilt, bool anyCounterAdvanced, long nowMs, long followUpAtMs) =>
        neverBuilt || anyCounterAdvanced || (followUpAtMs != Never && nowMs >= followUpAtMs);

    /// <summary>
    /// A hold belongs to the world epoch it was taken in. A different epoch means a world load
    /// happened and the held <c>Entity</c> now names something else (or nothing).
    /// </summary>
    public static bool SameEpoch(int heldEpoch, int currentEpoch) => heldEpoch == currentEpoch;

    /// <summary>
    /// True when an <c>AbilityCooldownState</c> is not already the zero state. Skipping the write
    /// when nothing changed keeps the component's change version (and so the snapshot the server
    /// sends) quiet while the ability is idle. NaN counts as needing a write.
    /// </summary>
    public static bool NeedsCooldownWrite(double cooldownEndTime, float currentCooldown) =>
        !(cooldownEndTime == 0.0) || !(currentCooldown == 0f);

    /// <summary>
    /// The charge count to write, or -1 for "leave it". Nothing is written for an ability without
    /// charges (<paramref name="maxCharges"/> 0 or less) or one already full; a maximum above the
    /// synced range is clamped to <see cref="MaxSyncedCharges"/>.
    /// </summary>
    public static int ChargesToWrite(int currentCharges, int maxCharges)
    {
        if (maxCharges <= 0) return -1;
        int target = maxCharges > MaxSyncedCharges ? MaxSyncedCharges : maxCharges;
        return currentCharges < target ? target : -1;
    }

    /// <summary>The key a character is held under: index and version, so a reused index is a different key.</summary>
    public static long Key(int index, int version) => ((long)index << 32) | (uint)version;

    /// <summary>
    /// Per-character holders. A character stays cooldown-free while any holder remains; each
    /// holder is released once. <typeparamref name="THolder"/> is compared by reference.
    /// </summary>
    public sealed class Holds<THolder> where THolder : class
    {
        private readonly Dictionary<long, List<THolder>> _byCharacter = new();

        /// <summary>Characters with at least one holder.</summary>
        public int CharacterCount => _byCharacter.Count;

        /// <summary>The held character keys, copied.</summary>
        public long[] Keys()
        {
            var keys = new long[_byCharacter.Count];
            _byCharacter.Keys.CopyTo(keys, 0);
            return keys;
        }

        /// <summary>Add a holder. Returns true when this is the character's first holder.</summary>
        public bool Add(long key, THolder holder)
        {
            if (holder == null) throw new ArgumentNullException(nameof(holder));
            if (!_byCharacter.TryGetValue(key, out var list))
            {
                list = new List<THolder>();
                _byCharacter[key] = list;
            }
            foreach (var h in list)
                if (ReferenceEquals(h, holder)) return false;
            list.Add(holder);
            return list.Count == 1;
        }

        /// <summary>
        /// Remove a holder. Returns true when that was the character's last holder, i.e. the
        /// character should stop being written. Removing an unknown holder is a no-op (false).
        /// </summary>
        public bool Remove(long key, THolder holder)
        {
            if (!_byCharacter.TryGetValue(key, out var list)) return false;
            int index = -1;
            for (int i = 0; i < list.Count; i++)
                if (ReferenceEquals(list[i], holder)) { index = i; break; }
            if (index < 0) return false;
            list.RemoveAt(index);
            if (list.Count > 0) return false;
            _byCharacter.Remove(key);
            return true;
        }

        /// <summary>True when <paramref name="key"/> has any holder.</summary>
        public bool IsHeld(long key) => _byCharacter.ContainsKey(key);

        /// <summary>True when <paramref name="key"/> has a holder matching <paramref name="match"/>.</summary>
        public bool IsHeldBy(long key, Func<THolder, bool> match)
        {
            if (!_byCharacter.TryGetValue(key, out var list)) return false;
            foreach (var h in list)
                if (match(h)) return true;
            return false;
        }

        /// <summary>Drop every holder of <paramref name="key"/> and return them.</summary>
        public List<THolder> Drop(long key)
        {
            if (!_byCharacter.TryGetValue(key, out var list)) return new List<THolder>();
            _byCharacter.Remove(key);
            return list;
        }

        /// <summary>Drop every holder of every character and return them.</summary>
        public List<THolder> DropAll()
        {
            var all = new List<THolder>();
            foreach (var list in _byCharacter.Values) all.AddRange(list);
            _byCharacter.Clear();
            return all;
        }
    }

    // ---- text ------------------------------------------------------------------------------------

    private static string Fmt(int index, int version) =>
        index.ToString(CultureInfo.InvariantCulture) + ":" + version.ToString(CultureInfo.InvariantCulture);

    /// <summary>Refusal: no server world, or <c>ServerScriptMapper</c> is not in it yet.</summary>
    public static string NotReadyRefusal() =>
        "force cast refused: the server world or its ServerScriptMapper is not ready.";

    /// <summary>Refusal: the character entity is gone.</summary>
    public static string MissingCharacterRefusal(int index, int version) =>
        "force cast refused: character " + Fmt(index, version) + " does not exist.";

    /// <summary>Log line after <c>ServerGameManager.ForceCastAbilityGroup</c> returned.</summary>
    public static string FormatForceCast(int groupHash, int index, int version, int castStartedBefore) =>
        "Abilities: ForceCastAbilityGroup " + groupHash.ToString(CultureInfo.InvariantCulture)
        + " on " + Fmt(index, version) + " (castStartedCounter was "
        + castStartedBefore.ToString(CultureInfo.InvariantCulture)
        + "; watch it advance for proof the cast started).";

    /// <summary>Warning when an owned-entity rebuild fails and the previous lists are kept.</summary>
    public static string FormatRebuildFailed(string error) =>
        "Abilities: no-cooldowns rebuild of the owned-entity lists failed (" + error
        + "); keeping the previous lists. Logged once.";

    /// <summary>Log line when a character starts being held cooldown-free.</summary>
    public static string FormatHold(int index, int version) =>
        "Abilities: no-cooldowns on " + Fmt(index, version) + ".";

    /// <summary>Log line when a character stops being held, with the reason.</summary>
    public static string FormatRelease(int index, int version, string why) =>
        "Abilities: no-cooldowns released on " + Fmt(index, version) + " (" + why + "); cooldowns resume normally.";
}
