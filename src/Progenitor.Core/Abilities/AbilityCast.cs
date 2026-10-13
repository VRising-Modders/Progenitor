using System;
using Progenitor.Ecs;
using ProjectM;
using ProjectM.Scripting;
using Stunlock.Core;
using Unity.Entities;

namespace Progenitor.Abilities;

/// <summary>
/// Cast a vanilla ability group on a character, on demand, from server code.
///
/// <para>This is the framework's answer to "make the player do that thing the boss does". It does
/// not rebuild an effect out of sequences, particles and beams — an approach that reached a
/// visible partial result and never a faithful one (research <c>charm-catalogue-correction.md</c>).
/// It hands the group to the game's own cast pipeline, so the visuals, the timing, the damage and
/// the network replication are the real ability's, not an imitation of it.</para>
///
/// <para><b>The seam</b> is <c>AbilityBar_Shared.ForceCastGroup</c>, a <c>PrefabGUID</c> the cast
/// pipeline consumes and clears. See <see cref="AbilityCastPolicy"/> for the measurement behind
/// that claim and for why a pending request is refused rather than overwritten. The offset is a
/// dump-resolved layout constant (<see cref="Layout.AbilityBarForceCastGroupOffset"/>), never a
/// literal, so a game update fails <c>layoutgen --check</c> instead of writing wrong memory.</para>
///
/// <para><b>An off-bar group does not start through <see cref="Cast"/>.</b> Live, 2026-09-25
/// (isolated server): <c>AB_Unholy_CorruptedSkull_AbilityGroup</c> (-1204819086) written to
/// <c>ForceCastGroup</c> on a player whose bar lacks it never moved <c>CastStartedCounter</c>;
/// the point emote (-452406649) did start. An earlier (2026-09-06) run recorded an off-bar start;
/// treat that as not reproducible and assume the character must own the group. To start a group
/// the character does not have, use <see cref="ForceCastGroup"/> (<c>ctx.Abilities.ForceCast</c>).</para>
///
/// <para><b>Raw write, deliberately.</b> <c>AbilityBar_Shared</c> carries <c>bool</c> fields, so it
/// is not <c>unmanaged</c> in C# and the toolkit's <c>Write&lt;T&gt;</c> cannot take it (interop
/// trap 9). This writes the one <c>int</c> at its dump-resolved offset through
/// <c>GetComponentDataRawRW</c>, the same route <c>PrefabApplicator</c> uses, and touches nothing
/// else in the component.</para>
/// </summary>
public static class AbilityCast
{
    private struct PendingRequest
    {
        public int Value;
        public int Counter;
    }

    /// <summary>
    /// The last forced request this process wrote per character, with the cast counter at the
    /// time. It is what lets <see cref="Cast"/> tell "somebody has a live cast pending" from "our
    /// own request was declined and left behind".
    /// </summary>
    private static readonly System.Collections.Generic.Dictionary<long, PendingRequest> LastRequest = new();

    /// <summary>Drops the per-character request bookkeeping. Called on Core uninstall.</summary>
    public static void ResetForTests() => LastRequest.Clear();

    /// <summary>
    /// Ask the game to start one cast of <paramref name="group"/> on <paramref name="character"/>
    /// by writing <c>AbilityBar_Shared.ForceCastGroup</c>. Returns false with <paramref name="why"/>
    /// set on every refusal; never throws for an ordinary bad argument.
    ///
    /// <para><b>True means "request written", not "cast started".</b> The pipeline may still
    /// discard it, silently and with the field back at 0, when the character is already casting or
    /// when the group is not on the character's ability bar (live 2026-09-25: an off-bar
    /// <c>AB_Unholy_CorruptedSkull_AbilityGroup</c> never started). The only proof of a start is
    /// <see cref="CastStartedCounter"/> advancing. For a group that is not on the bar use
    /// <see cref="ForceCastGroup"/>.</para>
    /// </summary>
    public static unsafe bool Cast(World? world, Entity character, PrefabGUID group, out string why)
    {
        if (!AbilityCastPolicy.IsCastableGroup(group.GuidHash))
        {
            why = AbilityCastPolicy.EmptyGroupRefusal();
            return false;
        }

        if (world == null || !world.IsCreated)
        {
            why = AbilityCastPolicy.NoWorldRefusal();
            return false;
        }

        try
        {
            var em = world.EntityManager;
            if (!em.Exists(character))
            {
                why = AbilityCastPolicy.DeadCharacterRefusal(character.Index, character.Version);
                return false;
            }

            var barType = ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(character, barType))
            {
                why = AbilityCastPolicy.NoAbilityBarRefusal(character.Index, character.Version);
                return false;
            }

            var typeIndex = ComponentIndex.IndexOf<AbilityBar_Shared>();
            byte* bar = (byte*)em.GetComponentDataRawRW(character, typeIndex);
            if (bar == null)
            {
                why = AbilityCastPolicy.RawReadRefusal(character.Index, character.Version);
                return false;
            }

            int pending = *(int*)(bar + Layout.AbilityBarForceCastGroupOffset);
            int castStartedBefore = *(int*)(bar + Layout.AbilityBarCastStartedCounterOffset);
            long key = ((long)character.Index << 32) | (uint)character.Version;

            if (AbilityCastPolicy.HasPendingCast(pending))
            {
                // The field is NOT always consumed. A request the pipeline declines is left
                // sitting there, and because every later call refuses on a non-zero field, one
                // declined cast otherwise wedges this character forever. (Measured: a forced dash
                // was declined while the character was not simulating, and every subsequent
                // request for minutes afterwards refused with "already holds".)
                //
                // Ours and dead is a state we can recognize exactly: the pending value is the one
                // we wrote, and the cast-started counter has not moved since we wrote it, so
                // nothing consumed it. Anything else - a value we did not write, or a counter that
                // has advanced - still refuses, because that may be somebody's live cast.
                bool ourStaleRequest =
                    LastRequest.TryGetValue(key, out var last)
                    && last.Value == pending
                    && last.Counter == castStartedBefore;

                if (!ourStaleRequest)
                {
                    why = AbilityCastPolicy.PendingRefusal(pending);
                    return false;
                }

                Plugin.Logger?.LogWarning(
                    "Abilities: clearing our own stale ForceCastGroup " + pending
                    + " on " + character.Index + ":" + character.Version
                    + " (the pipeline never took it; cast counter still " + castStartedBefore + ").");
            }
            *(int*)(bar + Layout.AbilityBarForceCastGroupOffset) = group.GuidHash;

            LastRequest[key] = new PendingRequest { Value = group.GuidHash, Counter = castStartedBefore };
            Plugin.Logger?.LogInfo(
                AbilityCastPolicy.FormatCast(group.GuidHash, character.Index, character.Version, castStartedBefore));
            why = "";
            return true;
        }
        catch (Exception e)
        {
            why = "ability cast threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Start <paramref name="group"/> on <paramref name="character"/> through
    /// <c>ServerGameManager.ForceCastAbilityGroup(Entity, PrefabGUID)</c>,
    /// the game's own scripting entry point. Server only; main thread. Returns false with
    /// <paramref name="why"/> when the group is empty, the world or its <c>ServerScriptMapper</c> is
    /// not ready, the character is gone or has no <c>AbilityBar_Shared</c>, or the call threw.
    ///
    /// <para><b>Difference from <see cref="Cast"/>.</b> Live, 2026-09-25 (isolated server): this
    /// started <c>AB_Unholy_CorruptedSkull_AbilityGroup</c> (-1204819086) on a player whose bar did
    /// not hold it (<c>CastStartedCounter</c> advanced, the projectile flew and hit) where
    /// <see cref="Cast"/> did not. True still means "the call returned", not "the cast started":
    /// compare <see cref="CastStartedCounter"/> before and a frame after.</para>
    ///
    /// <para><b>Aim.</b> Aimed abilities use the character's <c>EntityInput.AimPosition</c>. For a
    /// player that comes from their client (the mouse), so a forced projectile goes wherever the
    /// player is pointing, not at a target this call chooses. Cooldowns still apply; see
    /// <c>ctx.Abilities.NoCooldowns</c>.</para>
    /// </summary>
    public static bool ForceCastGroup(World? world, Entity character, PrefabGUID group, out string why)
    {
        if (!AbilityCastPolicy.IsCastableGroup(group.GuidHash))
        {
            why = AbilityCastPolicy.EmptyGroupRefusal();
            return false;
        }
        if (world == null || !world.IsCreated)
        {
            why = AbilityCooldownPolicy.NotReadyRefusal();
            return false;
        }

        try
        {
            var em = world.EntityManager;
            if (character == Entity.Null || !em.Exists(character))
            {
                why = AbilityCooldownPolicy.MissingCharacterRefusal(character.Index, character.Version);
                return false;
            }
            if (!em.HasComponent(character, ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadOnly)))
            {
                why = AbilityCastPolicy.NoAbilityBarRefusal(character.Index, character.Version);
                return false;
            }

            var mapper = world.GetExistingSystemManaged<ServerScriptMapper>();
            if (mapper == null)
            {
                why = AbilityCooldownPolicy.NotReadyRefusal();
                return false;
            }

            int before = CastStartedCounter(world, character);
            mapper.GetServerGameManager().ForceCastAbilityGroup(character, group);
            Plugin.Logger?.LogDebug(
                AbilityCooldownPolicy.FormatForceCast(group.GuidHash, character.Index, character.Version, before));
            why = "";
            return true;
        }
        catch (Exception e)
        {
            why = "force cast threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// Rewrite an ability's cast time on its <b>prefab</b>, and hand back what was there so the
    /// caller can put it back.
    ///
    /// <para>A boss ability is authored for a boss: <c>AB_Wendigo_FrostNova_Cast</c> channels for
    /// 2 seconds with a 0.4 second recovery, which is right for a telegraphed encounter and wrong
    /// for anything that has to keep up with a player. A two-second channel also loses every race
    /// against the player's own movement, so an ability triggered off a dash either interrupts it
    /// or is interrupted by it.</para>
    ///
    /// <para><b>This edits the prefab, so it changes the ability for everything that casts it</b>,
    /// including the boss. It is the caller's job to restore the old values — the returned
    /// <paramref name="previousMax"/> / <paramref name="previousPost"/> exist for exactly that, and
    /// a consumer that does not restore them on unload leaves the encounter altered.</para>
    /// </summary>
    public static unsafe bool TrySetCastTime(
        World? world,
        PrefabGUID abilityPrefab,
        float maxCastTime,
        float postCastTime,
        out float previousMax,
        out float previousPost,
        out string why)
    {
        previousMax = -1f;
        previousPost = -1f;

        if (world == null || !world.IsCreated) { why = AbilityCastPolicy.NoWorldRefusal(); return false; }
        if (maxCastTime < 0f || postCastTime < 0f)
        {
            why = "cast time refused: negative times are not a shorter cast, they are a wrong write.";
            return false;
        }

        try
        {
            var em = world.EntityManager;
            if (!Ecs.PrefabLookup.TryGetEntity(world, abilityPrefab, out var prefab))
            {
                why = "cast time refused: no prefab for guid " + abilityPrefab.GuidHash + ".";
                return false;
            }

            var castType = ComponentIndex.Of<AbilityCastTimeData>(ComponentType.AccessMode.ReadWrite);
            if (!em.HasComponent(prefab, castType))
            {
                why = "cast time refused: prefab " + abilityPrefab.GuidHash + " has no AbilityCastTimeData; "
                    + "the cast time lives on the ability's *_Cast prefab, not on its AbilityGroup.";
                return false;
            }

            byte* p = (byte*)em.GetComponentDataRawRW(prefab, ComponentIndex.IndexOf<AbilityCastTimeData>());
            if (p == null) { why = "cast time refused: GetComponentDataRawRW returned null."; return false; }

            previousMax = *(float*)(p + Layout.AbilityCastTimeMaxOffset);
            previousPost = *(float*)(p + Layout.AbilityCastTimePostOffset);
            *(float*)(p + Layout.AbilityCastTimeMaxOffset) = maxCastTime;
            *(float*)(p + Layout.AbilityCastTimePostOffset) = postCastTime;

            Plugin.Logger?.LogInfo(
                "Abilities: cast time of " + abilityPrefab.GuidHash + " set to max=" + maxCastTime
                + " post=" + postCastTime + " (was max=" + previousMax + " post=" + previousPost
                + "). This is the shared prefab; restore it on unload.");
            why = "";
            return true;
        }
        catch (Exception e)
        {
            why = "cast time threw: " + e.Message;
            return false;
        }
    }

    /// <summary>
    /// The two fields that say "this character started a cast, and it was this group".
    ///
    /// <para>This is the seam to observe casts from, and the reason is measured rather than
    /// preferred: <c>AbilityCastStartedEvent</c> entities are created and consumed inside the frame
    /// that raises them, so a virtual system at <c>AfterSimulation</c> queries zero of them while
    /// the cast counter is visibly advancing. <c>CastStartedCounter</c> is monotonic and
    /// <c>CastGroupPrefabGuid</c> stays set for the length of the cast, so both survive to any
    /// later point in the frame.</para>
    ///
    /// <para><paramref name="castStartedCounter"/> is the edge to watch; compare it with the value
    /// you stored last frame. <paramref name="castGroup"/> is <c>PrefabGUID.Empty</c> when nothing
    /// is casting right now.</para>
    /// </summary>
    public static unsafe bool TryReadCastState(
        World? world, Entity character, out int castStartedCounter, out PrefabGUID castGroup)
    {
        castStartedCounter = -1;
        castGroup = default;
        if (world == null || !world.IsCreated) return false;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(character)) return false;
            if (!em.HasComponent(character, ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadOnly)))
                return false;
            byte* bar = (byte*)em.GetComponentDataRawRO(character, ComponentIndex.IndexOf<AbilityBar_Shared>());
            if (bar == null) return false;
            castStartedCounter = *(int*)(bar + Layout.AbilityBarCastStartedCounterOffset);
            castGroup = new PrefabGUID(*(int*)(bar + Layout.AbilityBarCastGroupPrefabGuidOffset));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The ability group sitting in one of a character's bar slots, read straight out of
    /// <c>AbilityGroupSlotBuffer</c>. Slot indices are per character: read the buffer and find the
    /// occupied ones rather than assuming a layout. Nine slots are on the bar
    /// (<c>AbilityGroupSlotBuffer.SLOTS_ON_BAR</c>); the buffer is longer because it also covers
    /// mounted and shapeshifted contexts.
    /// </summary>
    public static unsafe bool TryGetSlotGroup(World? world, Entity character, int slot, out PrefabGUID group)
    {
        group = default;
        if (world == null || !world.IsCreated || slot < 0) return false;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(character)) return false;
            var type = ComponentIndex.Of<AbilityGroupSlotBuffer>(ComponentType.AccessMode.ReadOnly);
            if (!em.HasComponent(character, type)) return false;
            var typeIndex = ComponentIndex.IndexOf<AbilityGroupSlotBuffer>();
            int length = em.GetBufferLength(character, typeIndex);
            if (slot >= length) return false;
            byte* raw = (byte*)em.GetBufferRawRO(character, typeIndex);
            if (raw == null) return false;
            group = new PrefabGUID(*(int*)(raw + (long)slot * Layout.AbilityGroupSlotStride));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// The character's cast-started counter, the read-back that shows a forced cast actually
    /// started. Returns -1 when it cannot be read.
    /// </summary>
    public static unsafe int CastStartedCounter(World? world, Entity character)
    {
        if (world == null || !world.IsCreated) return -1;
        try
        {
            var em = world.EntityManager;
            if (!em.Exists(character)) return -1;
            if (!em.HasComponent(character, ComponentIndex.Of<AbilityBar_Shared>(ComponentType.AccessMode.ReadOnly)))
                return -1;
            byte* bar = (byte*)em.GetComponentDataRawRO(character, ComponentIndex.IndexOf<AbilityBar_Shared>());
            if (bar == null) return -1;
            return *(int*)(bar + Layout.AbilityBarCastStartedCounterOffset);
        }
        catch
        {
            return -1;
        }
    }
}
