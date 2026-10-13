using System;

namespace Progenitor.Effects;

/// <summary>
/// Server-authored networked beam look. The only vanilla family with synced
/// <c>Start</c>/<c>End</c> is the professor coil beam
/// (<c>ProfessorCoilBeam_State_Shared</c>). Castle red / invalid
/// placement / coffin / arena-flag sequences are client-only (no networked endpoints);
/// those presets still spawn the coil entity so the graphic replicates, then a client
/// tint hook recolors the sequence GO (path 2). Path 3 (Content-clone swapping
/// <c>ProfessorCoilBeam_Client</c> sequence GUIDs) is documented, not implemented.
/// </summary>
public enum BeamPreset
{
    /// <summary>
    /// Thin red flowing look requested first (evidence 126 / build
    /// <c>InvalidPlaceSequence</c>). Spawns the coil networked entity and tags
    /// <c>BlastTime = 9001</c> so the client tints the replicated sequence red.
    /// </summary>
    CastleRed = 0,

    /// <summary>
    /// Native professor coil sequences (length buckets 1–4, Preview then Blast).
    /// No client tint; the baked sequence owns the color.
    /// </summary>
    Coil = 1,

    /// <summary>
    /// Coil entity tagged <c>BlastTime = 9002</c>; client tints the sequence GO blue
    /// (Cardinal-family color, not a second networked prefab).
    /// </summary>
    Blue = 2,

    /// <summary>
    /// Not a coil beam. The game's own <c>NetworkedSequence</c> entity
    /// (<c>SpawnSequenceForEntity</c>) whose two endpoints are synced
    /// <em>entities</em>, so the clients resolve both positions themselves every frame and the
    /// server writes nothing per tick. Spawned by <see cref="ServerBeam.Link"/>; the phase,
    /// tint markers and length buckets above do not apply.
    /// </summary>
    Link = 3,
}

/// <summary>
/// Which coil sequence a beam should show. The client picks the sequence itself from its
/// own local <c>Age</c> against the synced <c>PreviewTime</c>
/// (the client's native <c>ProfessorCoilSystem_Client</c> lambda
/// <c>OriginalLambdaBody</c>: spawn the Preview sequence once, then swap to Blast the first
/// frame <c>Age &gt; PreviewTime</c> and never again), so the phase is chosen by the
/// <c>PreviewTime</c> the server writes, not by a field of its own.
/// </summary>
public enum BeamPhase
{
    /// <summary>
    /// Preview sequence for the whole lifetime: <c>PreviewTime</c> pinned near its synced
    /// maximum so the client's local Age never passes it. This is the steady beam.
    /// </summary>
    Steady = 0,

    /// <summary>
    /// <c>PreviewTime</c> pinned near zero so the client swaps to the Blast sequence on its
    /// first frame. This is the vanilla coil discharge, a one-shot flash.
    /// </summary>
    Blast = 1,
}

/// <summary>
/// Dump-backed constants for <see cref="ServerBeam"/>. Length buckets, sync ranges,
/// tint markers in the spare <c>BlastTime</c> field, and the lateral offset for
/// <see cref="ServerBeam.Beams"/>.
/// </summary>
public static class BeamPresets
{
    /// <summary>
    /// <c>ProfessorCoilBeam_Client</c> has four <c>SequencePair</c>s
    /// (<c>SequencesLength1</c>..4). <c>GetSequence(float length, …)</c>
    /// picks a pair by length and
    /// <c>GetSequenceForState</c> returns Preview or Blast. Lengths
    /// past bucket 4 use bucket 4 (clamp inferred from four fields and no fifth).
    /// Exact meter cuts are not in the game's metadata.
    /// </summary>
    public const int LengthBucketCount = 4;

    /// <summary>
    /// Meter cuts between the four coil sequence buckets, from the client's native
    /// <c>ProfessorCoilSystem_Client.GetSequence</c>:
    /// <c>&lt; 12</c> → <c>SequencesLength1</c>, <c>&lt; 20</c> → 2, <c>&lt; 23.5</c> → 3,
    /// otherwise 4. EV11 recorded these as not in the metadata; EV14 confirmed them in the native code.
    /// The chosen sequence prefab has a <b>fixed</b> drawn length, so a coil beam whose
    /// length crosses a cut keeps the bucket it was spawned with until it is respawned.
    /// </summary>
    public const float LengthBucket1Max = 12f;

    /// <inheritdoc cref="LengthBucket1Max"/>
    public const float LengthBucket2Max = 20f;

    /// <inheritdoc cref="LengthBucket1Max"/>
    public const float LengthBucket3Max = 23.5f;

    /// <summary>Which <c>SequencesLength{N}</c> bucket (1–4) a beam of this length draws.</summary>
    public static int LengthBucket(float metres)
    {
        if (metres < LengthBucket1Max) return 1;
        if (metres < LengthBucket2Max) return 2;
        if (metres < LengthBucket3Max) return 3;
        return 4;
    }

    /// <summary><c>SyncRangedFloat(0, 20)</c> on <c>PreviewTime</c>.</summary>
    public const float PreviewTimeMax = 20f;

    /// <summary><c>SyncRangedFloat(0, 10000, 2)</c> on <c>BlastTime</c>.</summary>
    public const float BlastTimeMax = 10000f;

    /// <summary>
    /// The wire step of <c>BlastTime</c>, <b>measured</b> (R29 finding 16 / open question 4;
    /// EV15 and EV16 both called this an open question and picked marker spacings against it).
    ///
    /// <para><c>[SyncRangedFloat(0, 10000, 2, 1, null, null)]</c> (server metadata) binds
    /// <c>SyncRangedFloat.ctor(float min, float max, int decimals, OverflowHandling
    /// overflowHandling = 1, …)</c>, so the third argument is
    /// <b>decimals = 2</b>. The generated code calls
    /// <c>NetworkSync.WriteRangedFloat(ref NetBufferOut, value, min, max, decimals)</c>
    /// and <c>ReadRangedFloat</c>. In the server's native code
    /// (<c>ProjectM.Network.NetworkSync.WriteRangedFloat</c>) those are:</para>
    /// <code>
    /// scale = (int)powf(10f, decimals);              // 0x41200000 == 10f
    /// lo = (int)roundf(scale * min); hi = (int)roundf(scale * max);
    /// v  = clamp((int)(scale * value), lo, hi);      // truncation, nudged off both endpoints
    /// WriteRangedInteger(lo, hi, v);                 // read back as v / scale
    /// </code>
    /// <para>So the field is a fixed-point integer over <c>[0 … 1,000,000]</c> and the step is
    /// <b>0.01</b>. Three consequences, all of which R28 28.7 and R29 16 had to guess at:
    /// the EV15 markers one apart were in fact safe on the wire; the EV16 markers 100 apart are
    /// safe too; and 9200 is nowhere near the 10000 ceiling, so nothing clamps and the three
    /// markers cannot collapse onto one value. Every marker here is an exact integer, so it
    /// round-trips with no error at all.</para>
    /// </summary>
    public const float BlastTimeWireStep = 0.01f;

    /// <summary>
    /// Spare synced field used as a Progenitor ownership tag: every beam this module
    /// spawns writes one of <see cref="MarkerCoil"/> / <see cref="TintMarkerRed"/> /
    /// <see cref="TintMarkerBlue"/> so the client tick can tell a Progenitor beam from a
    /// vanilla coil discharge and only drive ours. The server never lets the game consume
    /// Blast (Age is reset every tick), so the field is free. Vanilla coil events use small
    /// BlastTime values (seconds of damage), not these.
    /// </summary>
    public const float MarkerCoil = 9000f;

    /// <summary>Progenitor marker that also asks the client for a red tint.</summary>
    public const float TintMarkerRed = 9100f;

    /// <summary>Progenitor marker that also asks the client for a blue tint.</summary>
    public const float TintMarkerBlue = 9200f;

    /// <summary>
    /// Distance between two markers. R28 finding 28.7: EV15's markers were <b>one</b> apart and
    /// the band was justified as "the widest unambiguous half-width for markers one apart" — a
    /// statement about the markers, not about the wire. <c>BlastTime</c> crosses the coil
    /// snapshot (<c>Snapshot_acdkgjbefjd</c>) and its quantization step
    /// is still an open question; a step coarser than 0.5 missed every marker and a step
    /// coarser than 1.0 made the three indistinguishable at <em>any</em> tolerance. Spacing the
    /// markers removes the guesswork instead of widening a band inside a gap that was too
    /// narrow to begin with.
    /// </summary>
    public const float MarkerSpacing = 100f;

    /// <summary>
    /// Which marker family this Core <b>writes</b>. 1 is EV15's 9000 / 9001 / 9002; 2 is
    /// EV16's 9000 / 9100 / 9200.
    ///
    /// <para><b>R29 finding 16 (EV17).</b> EV16 respaced the markers with nothing on the wire
    /// or in the handshake naming the change, so an old client against a new server (or the
    /// reverse) simply stopped seeing tints: 9100 and 9200 are not markers at all under
    /// scheme 1's ±0.5 band, and the only diagnostic was the "0 matched markers" line, which
    /// does not fire when <em>some</em> beams matched. The reader now accepts <b>both</b>
    /// families for one release and says so once when it sees the old one
    /// (<see cref="LegacyMarkerLog"/>), so a mixed pair degrades with a named reason instead of
    /// silently. Scheme 1 goes away in the release after the one that ships scheme 2.</para>
    /// </summary>
    public const int MarkerSchemeVersion = 2;

    /// <summary>Scheme-1 red tint marker (EV15). Read, never written; see <see cref="MarkerSchemeVersion"/>.</summary>
    public const float LegacyTintMarkerRed = 9001f;

    /// <summary>Scheme-1 blue tint marker (EV15). Read, never written.</summary>
    public const float LegacyTintMarkerBlue = 9002f;

    /// <summary>
    /// Band around a scheme-1 marker. The two are one apart, so this has to stay under 0.5 to
    /// keep them distinguishable; 0.4 leaves the midpoint (9001.5) in neither. That is far
    /// above <see cref="BlastTimeWireStep"/>, which is what makes the band adequate rather than
    /// what EV15 assumed it was.
    /// </summary>
    public const float LegacyMarkerTolerance = 0.4f;

    /// <summary>
    /// How far a wire value may sit from a marker and still be that marker. Well under half
    /// <see cref="MarkerSpacing"/>, so no value can be ambiguous between two <em>scheme-2</em>
    /// markers. It is 4000× <see cref="BlastTimeWireStep"/>, which is what makes it overlap the
    /// scheme-1 bands (R30 finding 20); <see cref="MarkerBucket(float, out bool)"/> resolves
    /// that overlap by exactness and <see cref="MarkerSchemeOverlapLog"/> reports it. Far
    /// above any plausible quantization of a field whose sibling <c>PreviewTime</c> is a
    /// <c>SyncRangedFloat(0, 20)</c>. R27 finding 16's other half stands: the comparison is a
    /// band, never <c>==</c>, and a miss is reported by the "0 matched markers" line.
    /// </summary>
    public const float MarkerTolerance = 40f;

    // R29 finding 16: `MarkerLow` / `MarkerHigh` are gone. They had no callers and, after the
    // respacing, described a contiguous 8960..9240 band that included the two gaps
    // `MarkerBucket` deliberately rejects — so anything that picked them up later would have
    // disagreed with the real predicate. `MarkerBucket` is the only marker test.

    /// <summary>Sample <c>.beam</c> default lifetime.</summary>
    public const float DefaultSeconds = 8f;

    /// <summary>
    /// <c>PreviewTime</c> written for a <see cref="BeamPhase.Steady"/> beam: the top of the
    /// <c>SyncRangedFloat(0, 20)</c> range. The client compares its own local
    /// <c>Age</c> (never synced: the coil snapshot <c>Snapshot_acdkgjbefjd</c> carries only
    /// <c>ProfessorCoilBeam_State_Shared</c>) against this value.
    /// </summary>
    public const float SteadyPreviewTime = PreviewTimeMax;

    /// <summary>
    /// Alternate steady <c>PreviewTime</c>. Written on every other keep-alive tick so the
    /// synced component always differs from the last snapshot even when both endpoints are
    /// standing still; still above <see cref="MaxSeconds"/>, so the phase does not change.
    /// </summary>
    public const float SteadyPreviewTimeAlt = 19.5f;

    /// <summary><c>PreviewTime</c> written for a <see cref="BeamPhase.Blast"/> beam.</summary>
    public const float BlastPreviewTime = 0f;

    /// <summary>Alternate blast <c>PreviewTime</c>; one wire step, still an immediate blast.</summary>
    public const float BlastPreviewTimeAlt = 0.0625f;

    /// <summary>
    /// <c>NetworkedTimeout.TimeoutAfter</c> on the coil beam prefab (live-read on both
    /// sides 2026-09-03: <c>TimeoutAfter = 3</c>, <c>Enabled = true</c>). The client's
    /// <c>TimeoutSystem</c> (client, <c>[SpawnInWorld(4)]</c>,
    /// <c>AfterDeserializationGroup</c>) adds <c>DisabledDueToTimeout</c> + <c>Disabled</c>
    /// this many seconds after the last snapshot the client deserialized for the entity
    /// (<c>ReceivePacketSystem.ReceivePacketJob.GetNetworkedTimeout</c>).
    /// </summary>
    public const float NetworkedTimeoutSeconds = 3f;

    /// <summary>
    /// How often a beam forces a synced change even when nothing moved. Comfortably under
    /// <see cref="NetworkedTimeoutSeconds"/>: the server only serializes an entity to a user
    /// when its snapshot actually changed (<c>PrioritizeSystem</c> <c>UpToDateUserBitMask</c> /
    /// <c>SnapshotFrameChangedBuffer</c>), so a motionless beam that
    /// outlives the timeout is disabled on every client.
    /// </summary>
    public const float KeepAliveSeconds = 1f;

    /// <summary>
    /// Longest lifetime a steady beam can hold. The client leaves Preview the frame its local
    /// Age passes the synced <c>PreviewTime</c>, and <c>PreviewTime</c> cannot sync above
    /// <see cref="PreviewTimeMax"/>, so a coil beam is a steady line for at most this long.
    /// <see cref="ServerBeam"/> clamps to it and logs once.
    /// </summary>
    public const float MaxSeconds = 19f;

    /// <summary>Meters between parallel beams from <see cref="ServerBeam.Beams"/>.</summary>
    public const float DefaultSpacing = 0.4f;

    /// <summary>
    /// <c>Beams(count)</c> clamp. The game's metadata has no global concurrent cap
    /// (<c>ProfessorCoilEvent_Random.BeamCount</c> is per-event).
    /// </summary>
    public const int MaxCount = 8;

    /// <summary>Near-end lift so a character-to-rat beam is not buried in the floor.</summary>
    public static readonly EffectVec3 DefaultStartOffset = new(0f, 1f, 0f);

    /// <summary>Far-end lift for a standing unit.</summary>
    public static readonly EffectVec3 DefaultEndOffset = new(0f, 0.5f, 0f);

    /// <summary>
    /// Spawnable names tried when the caller does not pass a GUID. The live server
    /// prefers a <c>Prefab + ProfessorCoilBeam_Data_Server</c> query (the collection
    /// name is not among the game's string literals).
    /// </summary>
    public static readonly string[] CoilPrefabNames =
    {
        "ProfessorCoilBeam",
        "TM_ProfessorCoilBeam",
        "AB_ProfessorCoil_Beam",
        "TM_Castle_ProfessorCoilBeam",
    };

    /// <summary>
    /// Sequence GUIDs <see cref="ServerBeam.Link"/> can use. A <c>SequenceGUID.GuidHash</c> is
    /// an ordinary <c>PrefabGUID</c> hash: native <c>ProfessorCoilSystem_Client.OnUpdate</c>
    /// looks its sequence up in <c>PrefabLookupMap</c>, and the client's prefab map names those
    /// entities <c>(Sequence)&lt;asset&gt;</c> (live-read on the test client 2026-09-03).
    /// The sequence prefabs are <b>client-world only</b> (<c>[ComponentInWorld(4)]</c>), so the
    /// server cannot resolve or validate one — it just writes the hash into
    /// <c>SpawnSequenceForEntity.SequenceGuid</c> and the client resolves it.
    ///
    /// <para>Only a graph that reads its endpoints from <c>SequenceInstance</c>
    /// (a <c>Pull_Entity</c> component on the sequence prefab) tracks two entities.
    /// <c>(Sequence)AB_Charm_Channeling</c> carries <c>Pull_Entity</c> + <c>Pull_BuffTarget</c>
    /// and is the default; <c>(Sequence)ProfessorCoilBeam</c> does not (it is placed from
    /// <c>Position</c> / <c>Rotation</c> blackboard registers) and must not be used here.</para>
    /// </summary>
    public const int LinkSequenceCharmChanneling = -389499105;

    /// <summary><c>(Sequence)AB_Subdue_Channeling</c>, same family as <see cref="LinkSequenceCharmChanneling"/>.</summary>
    public const int LinkSequenceSubdueChanneling = -483640042;

    /// <summary><c>(Sequence)AB_Cardinal_SummonOrb_Beam_Buff</c> (the find-chest beam, evidence 80).</summary>
    public const int LinkSequenceCardinalOrb = 586432528;

    /// <summary><c>(Sequence)TM_WarEvent_PortalObject_Lesser</c>, the war-event portal beam.</summary>
    public const int LinkSequenceWarEventPortal = 1818774975;

    /// <summary>
    /// The build-mode <b>invalid placement</b> sequence: the two red flowing beams that rise
    /// from a placement preview and follow it. Live-captured (evidence 135) from
    /// <c>PreviewPlacementSequence { Sequence, ValidPlacement }</c> on a placement preview
    /// entity; both variants resolve to the prefab name
    /// <c>(Sequence)TM_EH_Mines_ExplosiveBarrel_Placeable_T01</c> and the sequence prefab
    /// carries <c>Pull_Entity</c>, so it draws on whatever
    /// <c>SequenceInstance.Target</c> holds. Single-target: <c>SecondaryTarget</c> stays null.
    /// </summary>
    public const int MarkSequenceRed = 29614748;

    /// <summary>The same asset's <b>valid placement</b> variant (blue).</summary>
    public const int MarkSequenceBlue = -450089786;

    /// <summary>What <see cref="ServerBeam.Mark"/> uses when the caller passes 0.</summary>
    public const int DefaultMarkSequence = MarkSequenceRed;

    /// <summary>Mark sequence for a preset: <see cref="BeamPreset.Blue"/> is blue, everything else red.</summary>
    public static int MarkSequenceFor(BeamPreset preset) =>
        preset == BeamPreset.Blue ? MarkSequenceBlue : MarkSequenceRed;

    /// <summary>
    /// What <see cref="ServerBeam.Link"/> uses when the caller passes 0. EV13 defaulted to
    /// <c>Script_WarEvent_PortalToGateSequenceData.SequenceGuid</c> (-287312447) read off a live
    /// war-event object; that hash is in <b>neither</b> side's <c>_PrefabGuidToEntityMap</c>
    /// (live-checked on both processes 2026-09-03), so the client could never resolve it and the
    /// link drew nothing.
    /// </summary>
    public const int DefaultLinkSequence = LinkSequenceCharmChanneling;

    /// <summary>Readable name for a cataloged link sequence, or the bare hash.</summary>
    public static string LinkSequenceName(int guid)
    {
        return guid switch
        {
            LinkSequenceCharmChanneling => "(Sequence)AB_Charm_Channeling",
            LinkSequenceSubdueChanneling => "(Sequence)AB_Subdue_Channeling",
            LinkSequenceCardinalOrb => "(Sequence)AB_Cardinal_SummonOrb_Beam_Buff",
            LinkSequenceWarEventPortal => "(Sequence)TM_WarEvent_PortalObject_Lesser",
            MarkSequenceRed => "(Sequence)TM_EH_Mines_ExplosiveBarrel_Placeable_T01 (invalid/red)",
            MarkSequenceBlue => "(Sequence)TM_EH_Mines_ExplosiveBarrel_Placeable_T01 (valid/blue)",
            _ => guid.ToString(System.Globalization.CultureInfo.InvariantCulture),
        };
    }

    /// <summary>
    /// BlastTime written for <paramref name="preset"/>. Coil uses <see cref="MarkerCoil"/>:
    /// no tint, but still a Progenitor marker so the client tick drives this beam's endpoints
    /// and leaves vanilla coil discharges alone.
    /// </summary>
    public static float BlastTimeFor(BeamPreset preset)
    {
        return preset switch
        {
            BeamPreset.CastleRed => TintMarkerRed,
            BeamPreset.Blue => TintMarkerBlue,
            _ => MarkerCoil,
        };
    }

    /// <summary>True when <paramref name="blastTime"/> is a Progenitor tint tag, not a vanilla blast duration.</summary>
    public static bool IsTintMarker(float blastTime)
    {
        float bucket = MarkerBucket(blastTime);
        return bucket == TintMarkerRed || bucket == TintMarkerBlue;
    }

    /// <summary>
    /// True when <paramref name="blastTime"/> marks a beam this module spawned (any preset).
    /// The client tick only touches these. Compared as a <b>band</b>, never with <c>==</c>:
    /// see <see cref="MarkerTolerance"/>.
    /// </summary>
    public static bool IsProgenitorMarker(float blastTime) => MarkerBucket(blastTime) != 0f;

    /// <summary>
    /// The marker <paramref name="blastTime"/> resolves to, or 0 when it is not one of ours.
    /// A wire value inside the band snaps to the nearest of
    /// <see cref="MarkerCoil"/> / <see cref="TintMarkerRed"/> / <see cref="TintMarkerBlue"/>.
    /// </summary>
    public static float MarkerBucket(float blastTime) => MarkerBucket(blastTime, out _);

    /// <summary>
    /// As <see cref="MarkerBucket(float)"/>, and reports whether the value came from the
    /// <b>previous</b> marker family (R29 finding 16). The answer is always a
    /// <see cref="MarkerSchemeVersion"/> marker, so every caller downstream — tint, tracking,
    /// the client tick — is scheme-agnostic; only the log line cares.
    ///
    /// <para><b>R30 finding 20: which scheme is tried first, and when.</b> The two schemes do
    /// overlap — every value in <c>[9000.6, 9001.4] ∪ [9001.6, 9002.4]</c> is inside a scheme-1
    /// band <em>and</em> inside scheme 2's ±40 band around <see cref="MarkerCoil"/> — and the
    /// old order resolved the whole overlap to scheme 1, so a value a scheme-2 server wrote as
    /// an untinted coil marker came back tinted with a permanent and false "this server runs an
    /// older Core" warning. The order is now by <em>exactness</em>, not by scheme:</para>
    ///
    /// <list type="number">
    /// <item>an exact scheme-2 marker (9000 / 9100 / 9200) wins, always;</item>
    /// <item>then an exact scheme-1 marker (9001 / 9002), reported as legacy — those are the
    /// only two values an older Core ever wrote, and <see cref="BlastTimeWireStep"/> is 0.01
    /// with every marker an exact integer, so they round-trip the wire exactly;</item>
    /// <item>then scheme 2's band, which is the only band left.</item>
    /// </list>
    ///
    /// <para>So the overlap is decided by a rule that names both schemes rather than by the
    /// order of two <c>if</c>s, and <see cref="MarkerSchemeOverlapLog"/> says which one won for
    /// any value the two schemes both claim. <see cref="MarkerCoil"/> is 9000 in both schemes,
    /// so it is never reported as legacy.</para>
    /// </summary>
    public static float MarkerBucket(float blastTime, out bool legacy)
    {
        legacy = false;
        if (float.IsNaN(blastTime) || float.IsInfinity(blastTime)) return 0f;

        // 1. The exact scheme-2 set, before anything else, so a marker this build writes can
        //    never be claimed by a legacy band (R30 finding 20).
        if (blastTime == MarkerCoil) return MarkerCoil;
        if (blastTime == TintMarkerRed) return TintMarkerRed;
        if (blastTime == TintMarkerBlue) return TintMarkerBlue;

        // 2. The exact scheme-1 values, and only those. A scheme-1 server wrote 9001 or 9002
        //    and the wire step is 0.01 with no rounding, so anything else in the legacy band is
        //    not a scheme-1 marker that drifted — it is an ordinary value inside scheme 2's band.
        if (blastTime == LegacyTintMarkerRed)
        {
            legacy = true;
            return TintMarkerRed;
        }

        if (blastTime == LegacyTintMarkerBlue)
        {
            legacy = true;
            return TintMarkerBlue;
        }

        float best = 0f;
        float bestDistance = float.MaxValue;
        for (int i = 0; i < 3; i++)
        {
            float marker = i == 0 ? MarkerCoil : i == 1 ? TintMarkerRed : TintMarkerBlue;
            float distance = MathF.Abs(blastTime - marker);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = marker;
        }

        // R28 finding 28.7: the gaps between markers are no longer inside the tolerance, so a
        // value between two of them is not "ours, rounded" — it is not ours.
        return bestDistance <= MarkerTolerance ? best : 0f;
    }

    /// <summary>
    /// Written once by the client tick when a replicated beam carries a scheme-1 marker: the
    /// server is on an older Core. The beam is still driven and still tinted — the point of
    /// accepting both families for one release — but the pair is mismatched and that is now
    /// said out loud instead of being inferred from missing tints (R29 finding 16).
    /// </summary>
    /// <summary>
    /// True when <paramref name="blastTime"/> is claimed by <b>both</b> marker schemes: inside a
    /// scheme-1 band <em>and</em> inside scheme 2's band. R30 finding 20 — the class comment's
    /// "no value can be ambiguous between two markers" is true within scheme 2 and false across
    /// the two schemes, and <see cref="MarkerTolerance"/> being 4000×
    /// <see cref="BlastTimeWireStep"/> is what makes the overlap wide.
    /// </summary>
    public static bool MarkerSchemesOverlap(float blastTime)
    {
        if (float.IsNaN(blastTime) || float.IsInfinity(blastTime)) return false;
        bool inLegacy =
            MathF.Abs(blastTime - LegacyTintMarkerRed) <= LegacyMarkerTolerance
            || MathF.Abs(blastTime - LegacyTintMarkerBlue) <= LegacyMarkerTolerance;
        if (!inLegacy) return false;
        float nearest = MathF.Min(
            MathF.Abs(blastTime - MarkerCoil),
            MathF.Min(MathF.Abs(blastTime - TintMarkerRed), MathF.Abs(blastTime - TintMarkerBlue)));
        return nearest <= MarkerTolerance;
    }

    /// <summary>
    /// Written once when a replicated <c>BlastTime</c> is claimed by both schemes, naming the
    /// scheme that won and the marker it resolved to. R30 finding 20: the overlap used to be
    /// resolved silently in scheme 1's favor, so the operator saw a tint and a false
    /// "older Core" warning with nothing saying an ambiguity had been decided at all.
    /// </summary>
    public static string MarkerSchemeOverlapLog(float blastTime)
    {
        float bucket = MarkerBucket(blastTime, out bool legacy);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return "Effects: server beam client saw a BlastTime ("
            + blastTime.ToString("0.##", inv)
            + ") that both marker schemes claim; scheme " + (legacy ? 1 : MarkerSchemeVersion)
            + " won and it reads as " + bucket.ToString("0", inv)
            + " (scheme 1 is accepted only for its exact values "
            + LegacyTintMarkerRed.ToString("0", inv) + "/"
            + LegacyTintMarkerBlue.ToString("0", inv) + ").";
    }

    public static string LegacyMarkerLog(float blastTime) =>
        "Effects: server beam client saw a marker-scheme-1 BlastTime ("
        + blastTime.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)
        + "); this server runs an older Core. Scheme " + MarkerSchemeVersion
        + " (" + MarkerCoil.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + TintMarkerRed.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        + "/" + TintMarkerBlue.ToString("0", System.Globalization.CultureInfo.InvariantCulture)
        + ") is what this build writes; scheme 1 is accepted for one release.";

    /// <summary>
    /// <c>PreviewTime</c> to write for <paramref name="phase"/>. <paramref name="alternate"/>
    /// flips between two values one wire step apart so a motionless beam still produces a
    /// snapshot diff (see <see cref="KeepAliveSeconds"/>); both values keep the same phase.
    /// </summary>
    public static float PreviewTimeFor(BeamPhase phase, bool alternate)
    {
        if (phase == BeamPhase.Blast)
            return alternate ? BlastPreviewTimeAlt : BlastPreviewTime;
        return alternate ? SteadyPreviewTimeAlt : SteadyPreviewTime;
    }

    /// <summary>
    /// Clamp a requested lifetime to what a steady coil beam can hold
    /// (<see cref="MaxSeconds"/>). Non-positive input falls back to
    /// <see cref="DefaultSeconds"/>.
    /// </summary>
    public static float ClampSeconds(float seconds)
    {
        if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            return DefaultSeconds;
        return seconds > MaxSeconds ? MaxSeconds : seconds;
    }

    /// <summary>
    /// Upper bound on a <see cref="BeamPreset.Link"/> / mark lifetime. The
    /// <c>NetworkedSequence</c> carrier has no <c>PreviewTime</c> ceiling, so it never hit
    /// <see cref="MaxSeconds"/> — and R27 finding 23 found that <c>.beam link 999999999</c>
    /// therefore produced a replicated, always-relevant entity with a ~31-year
    /// <c>LifeTime</c> (<c>TryUngateNoPlayersInRange</c> has already removed the proximity
    /// gate). Ten minutes is far longer than any beam a mod should hold and still bounded.
    /// </summary>
    public const float MaxLinkSeconds = 600f;

    /// <summary>
    /// Clamp a requested <c>ServerBeam.Link</c> / <c>ServerBeam.Mark</c> lifetime to
    /// <see cref="MaxLinkSeconds"/>. Non-positive, NaN and infinite input fall back to
    /// <see cref="DefaultSeconds"/>.
    /// </summary>
    public static float ClampLinkSeconds(float seconds)
    {
        if (seconds <= 0f || float.IsNaN(seconds) || float.IsInfinity(seconds))
            return DefaultSeconds;
        return seconds > MaxLinkSeconds ? MaxLinkSeconds : seconds;
    }

    /// <summary>Client tint color for a marker BlastTime, or null when the sequence should keep its baked look.</summary>
    public static EffectColor? TintForMarker(float blastTime)
    {
        float bucket = MarkerBucket(blastTime);
        if (bucket == TintMarkerRed) return BeamCatalog.GameRedTint;
        if (bucket == TintMarkerBlue) return BeamCatalog.GameBlueTint;
        return null;
    }

    /// <summary>
    /// Sideways offset for beam <paramref name="index"/> of <paramref name="count"/>
    /// so N beams sit in a line perpendicular to start→end (XZ, Y up). No allocation.
    /// A degenerate direction falls back to +X.
    /// </summary>
    public static EffectVec3 LateralOffset(EffectVec3 start, EffectVec3 end, int index, int count, float spacing)
    {
        if (count <= 1 || spacing == 0f) return default;
        float dx = end.X - start.X;
        float dy = end.Y - start.Y;
        float dz = end.Z - start.Z;
        // Perpendicular in XZ: (-dz, 0, dx). Keep a little of Y so a vertical beam still spreads.
        float sx = -dz;
        float sy = 0f;
        float sz = dx;
        float lenSq = (sx * sx) + (sz * sz);
        if (lenSq < 1e-8f)
        {
            sx = 1f;
            sy = 0f;
            sz = 0f;
            lenSq = 1f;
        }

        float inv = 1f / MathF.Sqrt(lenSq);
        sx *= inv;
        sz *= inv;
        float centered = index - ((count - 1) * 0.5f);
        float m = centered * spacing;
        _ = dy;
        return new EffectVec3(sx * m, sy * m, sz * m);
    }
}

/// <summary>
/// <b>R31 finding 17.</b> The client tick's marker classification, one call, with its two
/// one-shot diagnostics attached.
///
/// <para><see cref="BeamPresets.MarkerSchemesOverlap"/> and
/// <see cref="BeamPresets.MarkerSchemeOverlapLog"/> were added for R30 finding 20 and were
/// correct, but nothing in <c>src/</c> or <c>samples/</c> called either: the only client-tick
/// consumer called <see cref="BeamPresets.MarkerBucket"/> and logged the scheme-1 line only, so
/// an overlapping <c>BlastTime</c> resolved to <c>MarkerCoil</c> with no line at all while
/// <c>docs/effects.md</c> documented that line as something an operator sees. The behavioral
/// half of R30 finding 20 was fixed and the diagnostic half was dead.</para>
///
/// <para>Both are now produced here, and the production tick is the only caller. This is a
/// game-free struct, so the tick's classification step is driven directly by
/// <c>ServerBeamTests</c> - the tick body itself is inside <c>#if !PROGENITOR_TESTS</c>.</para>
/// </summary>
internal struct ClientMarkerLog
{
    private bool _legacyLogged;
    private bool _overlapLogged;

    /// <summary>
    /// Classify one replicated <c>BlastTime</c> exactly as the client tick does. Returns the
    /// bucket (0 = not one of ours, skip the beam) and, at most once each per Core generation,
    /// the scheme-1 warning and the scheme-overlap warning.
    /// </summary>
    internal float Classify(
        float blastTime, out bool legacy, out string? legacyLine, out string? overlapLine)
    {
        legacyLine = null;
        overlapLine = null;
        float bucket = BeamPresets.MarkerBucket(blastTime, out legacy);
        if (bucket == 0f) return bucket;

        // R29 finding 16: a scheme-1 marker means a server on an older Core. The beam is still
        // driven; the mismatch is named once instead of being left to be inferred from tints
        // that stopped appearing.
        if (legacy && !_legacyLogged)
        {
            _legacyLogged = true;
            legacyLine = BeamPresets.LegacyMarkerLog(blastTime);
        }

        // R30 finding 20 / R31 finding 17: the value is claimed by both schemes. The winner is
        // whatever MarkerBucket just decided; say so rather than resolving it silently.
        if (BeamPresets.MarkerSchemesOverlap(blastTime) && !_overlapLogged)
        {
            _overlapLogged = true;
            overlapLine = BeamPresets.MarkerSchemeOverlapLog(blastTime);
        }

        return bucket;
    }

    /// <summary>Back to the state a fresh Core generation starts in.</summary>
    internal void Reset()
    {
        _legacyLogged = false;
        _overlapLogged = false;
    }
}
