using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;

#if !PROGENITOR_TESTS
using ProjectM.Gameplay.WarEvents;
using ProjectM.Network;
using ProjectM.Shared.WarEvents;
using ProjectM.Shared.WorldEvents;
using Progenitor.Commands;
using Progenitor.Ecs;
using Progenitor.Events;
using Progenitor.Networking;
using Progenitor.Plugins;
using Progenitor.Systems;
using Progenitor.Ui;
using Unity.Entities;
using LocalizationApi = Progenitor.Localization.Localization;
#endif

namespace Progenitor.Sample;

/// <summary>
/// Job FRIFT1. Rift Incursion timers: the server reads the war-event scheduler, ships it as one
/// fixed-size packet, and the client renders two rows beside the vanilla map card.
///
/// <para><b>Why a packet at all.</b> The client holds exactly one <c>WarEvent_NetworkedData</c>
/// and zero <c>WarEvent</c> entities (research §3.1, live), so it can only ever see <em>one</em>
/// tier, and that record has no "which tier is next" field. The authoritative
/// <c>WarEventSystem.SchedulingData.NextWarEventType</c> lives on the server and does not
/// replicate (§3.2). Two tiers on screen therefore require a server→client message; anything else
/// is arithmetic dressed up as a schedule.</para>
///
/// <para><b>What is read, and how.</b> Everything is a managed read of public fields on a managed
/// system (<c>WarEventSystem.SchedulingData</c> / <c>.SpawnSettings</c>) plus two ordinary
/// queries (<c>WarEvent</c>, <c>WarEventSettingsComponent</c>). No raw write, no
/// <c>TypeManager</c> size question, nothing <c>Ecs/RawWriteGuard</c> needs to see. The durations
/// are enums in the settings struct, so the game's own <c>GetTotalDuration</c> /
/// <c>GetTotalTimeBetweenEvents</c> accessors do the mapping — never read the enum as a number.</para>
///
/// <para><b>Absolute UTC ticks, never a countdown.</b> War-event ticks are <c>DateTime</c> UTC
/// ticks, not the ECS <c>ServerTime</c> clock (research §2.4, live: five <c>ServerTime</c>
/// mappings all failed the sanity window). The packet carries absolute target ticks plus the
/// server's <c>DateTime.UtcNow.Ticks</c> at send, so the client computes one clock skew per
/// packet and then runs its own countdown locally. That is what lets the cadence be a handful of
/// packets an hour instead of one a second.</para>
///
/// <para><b>The rule this file exists to demonstrate: a derivation is never laundered into a
/// fact.</b> <c>SchedulingData</c> holds exactly one <c>NextWarEventType</c>, so at most one tier
/// has a real scheduled start. The other tier's next start can only be estimated from
/// duration + gap, and that estimate ignores the <c>WeekdayTime</c>/<c>WeekendTime</c> windows and
/// <c>SetNextEventSpawnTicksToValidTime</c> (§2.1). So the estimate travels with its own
/// <c>Derived</c> bit, renders with a trailing <c>~</c>, and <c>.rifts</c> says the word
/// "estimated". When neither a schedule nor a usable estimate exists the row reads
/// <c>not scheduled</c> — never a plausible-looking number (research §5 trap 12: a synthetic
/// countdown in exactly that case misleads).</para>
///
/// <para><b>Naming and countdown shape:</b> <c>T1</c> for <c>Minor</c> and <c>T2</c> for
/// <c>Major</c>, the optional <c>(57+)</c> gear-level suffix, and the <c>1h 9m</c> /
/// <c>19m 49s</c> / <c>12s</c> ladder, the labels players already use for rifts.</para>
///
/// <para><b>Client rows.</b> The two strings go to an <see cref="IRiftRowSink"/>, and the sink is
/// <c>Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, …)</c> from job FUI36: two rows beside the
/// vanilla map card, cloned from its own donor label so nothing is shipped. The handle is never
/// null and is disabled-but-valid on a server or with the Ui module skipped, so there is no
/// availability check here — but the container it owns lives under the vanilla map menu rather
/// than under this plugin's root, which is the one place <c>DestroyRoot</c> cannot reach, so it is
/// disposed explicitly on unload (research §4.4). When the handle is disabled the sink still
/// writes the rows to the log (<see cref="LogRowSink"/>), so the client half stays provable with
/// no UI at all. The rows must never appear on the HUD alert popup
/// (<c>AlertsParent.WarEventNode</c>, research §1.2 / negative control N4) — that is the site
/// resolver's job, and the reason the site is a closed enum rather than a type lookup.</para>
///
/// <para>The framing constants, the snapshot builder, the countdown arithmetic, the formatter,
/// the change/heartbeat decision and every line are game-free so the test project compiles this
/// file; the reads, the sends, the receive and the virtual systems are behind
/// <c>!PROGENITOR_TESTS</c>.</para>
/// </summary>
internal static class SampleRifts
{
    /// <summary>Log/grep area: <c>Sample rifts: …</c>.</summary>
    internal const string Area = "rifts";

    /// <summary>
    /// Server→client event id, ASCII <c>SmRf</c> ("sample rifts"), continuing the Sample's
    /// <c>Sm??</c> convention (<c>SmFx</c> / <c>SmUi</c> / <c>SmMd</c> / <c>SmNp</c> / <c>SmNr</c>).
    /// Outside Progenitor's reserved <c>0x5072</c> ("Pr**") range on purpose — a plugin
    /// registration inside it is refused (<c>CustomEvents.IsReservedEventId</c>).
    /// </summary>
    internal const int ScheduleEventId = 0x536D5266;

    /// <summary>Wire version in <see cref="RiftSchedule.Version"/>. Bump when a field changes meaning.</summary>
    internal const byte ProtocolVersion = 1;

    /// <summary>
    /// <see cref="RiftSchedule.NextType"/> when the scheduler has no next tier. It cannot be 0:
    /// <c>WarEventType.Major == 0</c> (server metadata), so a zeroed byte reads as Major.
    /// </summary>
    internal const byte NextTypeNone = 0xFF;

    /// <summary><see cref="RiftSchedule.Flags"/> bit 0: a Minor (T1) event is running now.</summary>
    internal const byte FlagMinorActive = 1 << 0;

    /// <summary><see cref="RiftSchedule.Flags"/> bit 1: a Major (T2) event is running now.</summary>
    internal const byte FlagMajorActive = 1 << 1;

    /// <summary><see cref="RiftSchedule.Flags"/> bit 2: <c>MinorNextTicks</c> is an estimate, not a schedule.</summary>
    internal const byte FlagMinorNextDerived = 1 << 2;

    /// <summary><see cref="RiftSchedule.Flags"/> bit 3: <c>MajorNextTicks</c> is an estimate, not a schedule.</summary>
    internal const byte FlagMajorNextDerived = 1 << 3;

    /// <summary>
    /// Longest quiet period before the server re-broadcasts an unchanged schedule. The heartbeat
    /// re-syncs clock skew and recovers a client that missed a change packet; it is not the
    /// display's refresh rate, because the client counts down locally.
    /// </summary>
    internal const double HeartbeatSeconds = 60;

    /// <summary>
    /// Floor between two broadcasts, whatever changed. A schedule that flaps (an event ending on
    /// the same tick the next is written, say) must not turn into a packet per evaluation.
    /// </summary>
    internal const double MinSendIntervalSeconds = 2;

    /// <summary>
    /// How old a received packet may be before the client stops trusting it. Three missed
    /// heartbeats: past that the rows say so rather than counting down from a dead server's
    /// numbers. Belt to the <c>WorldReady</c> braces (<see cref="ForgetSchedule"/>).
    /// </summary>
    internal const double StaleAfterSeconds = 3 * HeartbeatSeconds;

    /// <summary>Server evaluation cadence in virtual-system frames. Twice a second is ample for a schedule.</summary>
    internal const int ServerEvaluateFrames = 30;

    /// <summary>
    /// Client row-refresh cadence in frames, ~4 Hz. Not 60: the string only changes once a second
    /// and a TMP re-layout is not free (research §7).
    /// </summary>
    internal const int ClientRefreshFrames = 15;

    /// <summary>Server virtual-system name. Grep <c>sample.rifts</c>.</summary>
    internal const string ServerSystemName = "sample.rifts";

    /// <summary>Client virtual-system name.</summary>
    internal const string ClientSystemName = "sample.rifts.rows";

    /// <summary>The handshake answer status a user must have before the join send goes out.</summary>
    internal const string HandshakeOkToken = "ok";

    /// <summary>
    /// The two tiers, numbered as the game numbers them —
    /// <c>Major = 0</c>, <c>Minor = 1</c>, <c>Primal = 2</c> (server metadata). Declared here
    /// rather than reusing <c>ProjectM.Shared.WarEvents.WarEventType</c> so the snapshot builder
    /// and its tests are game-free, and so the <c>Major == 0</c> trap is stated once, in the type
    /// that carries it onto the wire.
    /// </summary>
    internal enum RiftTier : byte
    {
        /// <summary>T2, the higher-level tier. <b>Zero</b>, which is why the wire needs <see cref="NextTypeNone"/>.</summary>
        Major = 0,

        /// <summary>T1, the lower-level tier.</summary>
        Minor = 1,

        /// <summary>Not sent in v1: the enum has it, this server does not run it, and a field nobody can test will be wrong.</summary>
        Primal = 2,
    }

    /// <summary>Why a broadcast went out. Named so the log line says which rule fired.</summary>
    internal enum SendReason
    {
        /// <summary>Nothing to do this evaluation.</summary>
        None = 0,

        /// <summary>Nothing has ever been broadcast this Core generation.</summary>
        First = 1,

        /// <summary>At least one tick or level differs from the last broadcast.</summary>
        Change = 2,

        /// <summary>Unchanged, but <see cref="HeartbeatSeconds"/> have passed.</summary>
        Heartbeat = 3,
    }

    // ---- the packet -------------------------------------------------------------------------
    //
    // Game-free: no game type appears in it, so the tests construct and compare it directly. Fixed
    // size and `unmanaged`, which is what CustomEvents.SendToUser<T>/TryRead<T> require; TryRead
    // refuses any payload that is not exactly sizeof(RiftSchedule).

    /// <summary>
    /// <c>SmRf</c> v1. Five absolute-UTC-tick fields, the server's clock sample, both gear levels,
    /// and four bytes of framing: <see cref="PayloadBytes"/> = 56 on the wire.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct RiftSchedule
    {
        /// <summary><c>DateTime.UtcNow.Ticks</c> on the server at send. The client's skew reference.</summary>
        public long UtcTicks;

        /// <summary>When the running Minor (T1) event ends. 0 = none running.</summary>
        public long MinorEndTicks;

        /// <summary>When the next Minor (T1) event starts. 0 = unknown; see <see cref="FlagMinorNextDerived"/>.</summary>
        public long MinorNextTicks;

        /// <summary>When the running Major (T2) event ends. 0 = none running.</summary>
        public long MajorEndTicks;

        /// <summary>When the next Major (T2) event starts. 0 = unknown; see <see cref="FlagMajorNextDerived"/>.</summary>
        public long MajorNextTicks;

        /// <summary>Minor recommended (else minimum) gear level. 0 = unknown, and the row omits it.</summary>
        public int MinorLevel;

        /// <summary>Major recommended (else minimum) gear level. 0 = unknown, and the row omits it.</summary>
        public int MajorLevel;

        /// <summary><see cref="ProtocolVersion"/>.</summary>
        public byte Version;

        /// <summary>The scheduler's next tier as a <see cref="RiftTier"/>, or <see cref="NextTypeNone"/>.</summary>
        public byte NextType;

        /// <summary>The four <c>Flag…</c> bits.</summary>
        public byte Flags;

        /// <summary>Zero in v1. Room for Primal without a version bump of the tick fields.</summary>
        public byte Reserved;
    }

    /// <summary>
    /// Expected payload length of one <see cref="RiftSchedule"/> on the wire: <b>56</b>.
    ///
    /// <para><b>Not 52.</b> The fields add up to 52 (5 longs, 2 ints, 4 bytes), and that is the
    /// number a reader computes — but the five longs give the struct an 8-byte alignment, so the
    /// runtime rounds it to 56, and 56 is what the transport copies and what
    /// <c>CustomEvents.TryRead&lt;T&gt;</c> compares a payload length against. Writing the
    /// arithmetic here as an expression would have shipped a length gate that refuses every real
    /// packet. Same shape as the stride rule in <c>CLAUDE.md</c>: the size you can compute is not
    /// the size the runtime uses. The test pins the constant against
    /// <c>Unsafe.SizeOf&lt;T&gt;</c> and <c>Marshal.SizeOf&lt;T&gt;</c> so a field added in the
    /// middle cannot silently move the wire size.</para>
    /// </summary>
    internal const int PayloadBytes = 56;

    /// <summary>
    /// The length gate, in a pure function so the refusal is tested rather than eyeballed. The
    /// real receive still goes through <c>CustomEvents.TryRead&lt;RiftSchedule&gt;</c>, which
    /// applies the same rule against <c>sizeof(T)</c>; this exists so a wire-shape regression is a
    /// failing test and not a silent no-op on a player's client.
    /// </summary>
    internal static bool IsPayloadLength(int length) => length == PayloadBytes;

    /// <summary>Whether a version byte off the wire is one this client can read.</summary>
    internal static bool IsKnownVersion(byte version) => version == ProtocolVersion;

    // ---- the snapshot builder ---------------------------------------------------------------

    /// <summary>
    /// One tier as the server read it. <see cref="HasEvent"/> is "a <c>WarEvent</c> entity of this
    /// type exists", which is not the same as "it is running": the caller passes the raw ticks and
    /// <see cref="Build"/> decides.
    /// </summary>
    internal readonly struct TierReading
    {
        /// <summary>Whether a <c>WarEvent</c> entity of this tier was found.</summary>
        public readonly bool HasEvent;

        /// <summary><c>WarEvent.StartTimeTicks</c>, absolute UTC.</summary>
        public readonly long StartTicks;

        /// <summary><c>WarEvent.EndTimeTicks</c>, absolute UTC.</summary>
        public readonly long EndTicks;

        /// <summary>Recommended, else minimum, gear level from <c>WarEventSettingsComponent</c>. 0 = unknown.</summary>
        public readonly int Level;

        /// <summary>Create a reading.</summary>
        public TierReading(bool hasEvent, long startTicks, long endTicks, int level)
        {
            HasEvent = hasEvent;
            StartTicks = startTicks;
            EndTicks = endTicks;
            Level = level;
        }

        /// <summary>A tier with no event entity and no level.</summary>
        public static TierReading None => new(false, 0, 0, 0);

        /// <summary>A tier with no event entity but a known level.</summary>
        public static TierReading LevelOnly(int level) => new(false, 0, 0, level);
    }

    /// <summary>
    /// Two <c>WarEvent</c> entities of the same tier can exist for a moment around a transition.
    /// Prefer the one that is running; otherwise the one that ends later.
    /// </summary>
    internal static TierReading PickBetter(in TierReading a, in TierReading b, long nowUtcTicks)
    {
        if (!a.HasEvent) return b;
        if (!b.HasEvent) return a;
        bool aRunning = IsRunning(a, nowUtcTicks);
        bool bRunning = IsRunning(b, nowUtcTicks);
        if (aRunning != bRunning) return aRunning ? a : b;
        return b.EndTicks > a.EndTicks ? b : a;
    }

    /// <summary>Whether a reading describes an event that is running at <paramref name="nowUtcTicks"/>.</summary>
    internal static bool IsRunning(in TierReading tier, long nowUtcTicks) =>
        tier.HasEvent && tier.EndTicks > nowUtcTicks && tier.StartTicks <= nowUtcTicks;

    /// <summary>
    /// Build the packet from what the server read. Pure, so every branch below is a test rather
    /// than a live observation:
    ///
    /// <list type="bullet">
    /// <item><description>A tier that is running carries <c>EndTicks</c> and its Active bit, and
    /// <b>no</b> next start — the row shows the remaining time, and an estimate made while an
    /// event is running is the least reliable one there is.</description></item>
    /// <item><description>A tier the scheduler names in <c>NextWarEventType</c> carries
    /// <c>NextEventSpawnTicks</c> verbatim. That value has already been pushed past any closed
    /// <c>WeekdayTime</c>/<c>WeekendTime</c> window by
    /// <c>SetNextEventSpawnTicksToValidTime</c>, which is why it is read rather than
    /// computed.</description></item>
    /// <item><description>The other tier is estimated as
    /// <c>nextSpawn + duration(nextTier) + gap(thisTier)</c> (research §2.6) and gets its Derived
    /// bit. If either accessor returned a non-positive number the estimate is <b>not</b> made and
    /// the field stays 0.</description></item>
    /// <item><description>No scheduling data at all: both next fields are 0. The row will say
    /// "not scheduled". It will not say a number.</description></item>
    /// </list>
    /// </summary>
    internal static RiftSchedule Build(
        long nowUtcTicks,
        in TierReading minor,
        in TierReading major,
        bool hasScheduling,
        long nextSpawnTicks,
        RiftTier nextType,
        double minorDurationSeconds,
        double majorDurationSeconds,
        double minorGapSeconds,
        double majorGapSeconds)
    {
        bool scheduling = hasScheduling && nextSpawnTicks > 0
                          && (nextType == RiftTier.Minor || nextType == RiftTier.Major);

        bool minorActive = IsRunning(minor, nowUtcTicks);
        bool majorActive = IsRunning(major, nowUtcTicks);

        long minorNext = 0, majorNext = 0;
        bool minorDerived = false, majorDerived = false;

        if (!minorActive)
        {
            if (scheduling && nextType == RiftTier.Minor)
            {
                minorNext = nextSpawnTicks;
            }
            else if (scheduling)
            {
                // The scheduler is busy with the other tier; ours is arithmetic and says so.
                minorNext = Estimate(nextSpawnTicks, majorDurationSeconds, minorGapSeconds);
                minorDerived = minorNext > 0;
            }
        }

        if (!majorActive)
        {
            if (scheduling && nextType == RiftTier.Major)
            {
                majorNext = nextSpawnTicks;
            }
            else if (scheduling)
            {
                majorNext = Estimate(nextSpawnTicks, minorDurationSeconds, majorGapSeconds);
                majorDerived = majorNext > 0;
            }
        }

        byte flags = 0;
        if (minorActive) flags |= FlagMinorActive;
        if (majorActive) flags |= FlagMajorActive;
        if (minorDerived) flags |= FlagMinorNextDerived;
        if (majorDerived) flags |= FlagMajorNextDerived;

        return new RiftSchedule
        {
            UtcTicks = nowUtcTicks,
            MinorEndTicks = minorActive ? minor.EndTicks : 0,
            MinorNextTicks = minorNext,
            MajorEndTicks = majorActive ? major.EndTicks : 0,
            MajorNextTicks = majorNext,
            MinorLevel = minor.Level < 0 ? 0 : minor.Level,
            MajorLevel = major.Level < 0 ? 0 : major.Level,
            Version = ProtocolVersion,
            NextType = scheduling ? (byte)nextType : NextTypeNone,
            Flags = flags,
            Reserved = 0,
        };
    }

    /// <summary>
    /// <c>nextSpawn + otherDuration + ourGap</c>, in ticks, or 0 when either accessor gave nothing
    /// usable. Refusing to guess is the whole point: a zero here becomes "not scheduled".
    /// </summary>
    private static long Estimate(long nextSpawnTicks, double otherTierDurationSeconds, double ourGapSeconds)
    {
        if (otherTierDurationSeconds <= 0 || ourGapSeconds <= 0) return 0;
        double seconds = otherTierDurationSeconds + ourGapSeconds;
        if (double.IsNaN(seconds) || double.IsInfinity(seconds)) return 0;
        double ticks = seconds * TimeSpan.TicksPerSecond;
        if (ticks > long.MaxValue - nextSpawnTicks) return 0;
        return nextSpawnTicks + (long)ticks;
    }

    // ---- the clock --------------------------------------------------------------------------

    /// <summary>
    /// One-off clock skew for a packet: how far the server's <c>UtcNow</c> is ahead of ours.
    /// Everything after this is local arithmetic, which is why a packet a minute old still renders
    /// a smooth countdown.
    /// </summary>
    internal static long SkewTicks(long packetUtcTicks, long localUtcTicksAtReceive) =>
        packetUtcTicks - localUtcTicksAtReceive;

    /// <summary>
    /// Seconds from now until an absolute server tick, corrected by <paramref name="skewTicks"/>.
    /// Negative when the moment has passed; callers clamp where a negative would be nonsense.
    /// </summary>
    internal static double SecondsUntil(long targetTicks, long localUtcNowTicks, long skewTicks) =>
        (targetTicks - (localUtcNowTicks + skewTicks)) / (double)TimeSpan.TicksPerSecond;

    /// <summary>
    /// Whether a held packet is too old to render. A client that has been sitting on a schedule
    /// for three missed heartbeats is looking at a server it is no longer hearing from.
    /// </summary>
    internal static bool IsStale(long localReceiveTicks, long localUtcNowTicks) =>
        (localUtcNowTicks - localReceiveTicks) / (double)TimeSpan.TicksPerSecond >= StaleAfterSeconds;

    // ---- formatting -------------------------------------------------------------------------

    /// <summary>
    /// <c>T1</c> / <c>T2</c> / <c>Primal</c>, the labels players already use.
    /// </summary>
    internal static string TierName(RiftTier tier) => tier switch
    {
        RiftTier.Minor => "T1",
        RiftTier.Major => "T2",
        RiftTier.Primal => "Primal",
        _ => tier.ToString(),
    };

    /// <summary>
    /// <c>1h 9m</c> / <c>19m 49s</c> / <c>12s</c>: hours drop the seconds because
    /// an hour-scale countdown that ticks per second is noise. Negatives clamp to zero.
    /// </summary>
    internal static string FormatDuration(double seconds)
    {
        if (double.IsNaN(seconds) || seconds < 0) seconds = 0;
        if (seconds > int.MaxValue) seconds = int.MaxValue;
        int total = (int)Math.Floor(seconds);
        int h = total / 3600;
        int m = total % 3600 / 60;
        int s = total % 60;
        if (h > 0) return N(h) + "h " + N(m) + "m";
        if (m > 0) return N(m) + "m " + N(s) + "s";
        return N(s) + "s";
    }

    /// <summary>The marker a derived next start always carries. A schedule never has it.</summary>
    internal const string DerivedMarker = " ~";

    /// <summary>What a row says when there is neither a schedule nor a usable estimate.</summary>
    internal const string NotScheduled = "- not scheduled";

    /// <summary>What a row says before the first packet arrives, or after one goes stale.</summary>
    internal const string NoData = "- waiting for the server";

    // ---- the row templates (job FEX9, R60 finding 9) ------------------------------------------

    /// <summary>
    /// Job FEX9 (R60 finding 9). The four row templates are localization keys, loaded from this
    /// plugin's <c>localization/en.json</c> like the Ember Shard's name and read back through the
    /// Localization module. These rows sit <em>inside</em> a card the game has already localized
    /// for the player, which is the one place in the Sample where an English literal is visibly
    /// out of place.
    ///
    /// <para><b>Placeholders</b>: <c>{tier}</c> is <see cref="TierName"/> (<c>T1</c>),
    /// <c>{level}</c> is the gear-level <em>suffix</em> including its space and parentheses
    /// (<c>" (57+)"</c>, and empty at level 0, which is how a zero level keeps omitting it rather
    /// than printing <c>(0+)</c>), and <c>{time}</c> is <see cref="FormatDuration"/>. Unknown
    /// placeholders are left alone; a missing one simply does not appear.</para>
    ///
    /// <para>The English constants below are the fallback, not decoration: on the server there is
    /// no game string table at all (<c>SampleLocalization</c>'s "what each side does"), so the
    /// resolver answers null there and these stand. That is also what a translator dropping a
    /// file with three of the four keys gets for the fourth.</para>
    /// </summary>
    internal const string NextKey = "sample.rifts.next";

    /// <inheritdoc cref="NextKey"/>
    internal const string RemainingKey = "sample.rifts.remaining";

    /// <inheritdoc cref="NextKey"/>
    internal const string NotScheduledKey = "sample.rifts.notScheduled";

    /// <inheritdoc cref="NextKey"/>
    internal const string WaitingKey = "sample.rifts.waiting";

    /// <summary>English for <see cref="NextKey"/>, and the fallback when it is not registered.</summary>
    internal const string NextEnglish = "{tier}{level} next in {time}";

    /// <summary>English for <see cref="RemainingKey"/>.</summary>
    internal const string RemainingEnglish = "{tier}{level} remaining {time}";

    /// <summary>English for <see cref="NotScheduledKey"/>.</summary>
    internal const string NotScheduledEnglish = "{tier}{level} " + NotScheduled;

    /// <summary>English for <see cref="WaitingKey"/>. No level: this row has no schedule to read one from.</summary>
    internal const string WaitingEnglish = "{tier} " + NoData;

    /// <summary>
    /// Where a template comes from. Set to the Localization read on <c>Attach</c> and cleared on
    /// <c>Detach</c>, so a template never outlives the generation that registered the strings;
    /// null (the test default, and any moment outside a plugin's life) means English.
    /// A resolver returns null or empty for a key it does not know.
    /// </summary>
    internal static Func<string, string?>? TemplateResolver;

    /// <summary>The registered template for a key, or <paramref name="english"/>.</summary>
    internal static string Template(string key, string english)
    {
        try
        {
            string? text = TemplateResolver?.Invoke(key);
            if (!string.IsNullOrEmpty(text)) return text!;
        }
        catch
        {
            // A resolver that throws is a broken translation, not a reason to render nothing.
        }
        return english;
    }

    /// <summary>Substitute the three placeholders. Ordinal, and a template may use each once, twice or not at all.</summary>
    internal static string Format(string template, string tier, string level, string time) =>
        (template ?? "")
            .Replace("{tier}", tier, StringComparison.Ordinal)
            .Replace("{level}", level, StringComparison.Ordinal)
            .Replace("{time}", time, StringComparison.Ordinal);

    /// <summary>
    /// One row. <c>T1 (57+) next in 1h 9m</c> / <c>T2 (80+) remaining 19m 49s</c> /
    /// <c>T1 (57+) next in 1h 9m ~</c> (estimated) / <c>T1 (57+) - not scheduled</c>.
    /// A zero level omits the suffix rather than printing <c>(0+)</c>.
    /// The <see cref="DerivedMarker"/> is appended outside the template: it is a marker this file
    /// owns the meaning of, not text a translator should be able to drop.
    /// </summary>
    internal static string Row(
        RiftTier tier, int level, bool active, double remainingSeconds,
        bool hasNext, double nextInSeconds, bool derived)
    {
        string name = TierName(tier);
        string suffix = level > 0 ? " (" + N(level) + "+)" : "";
        if (active && remainingSeconds > 0)
            return Format(Template(RemainingKey, RemainingEnglish), name, suffix, FormatDuration(remainingSeconds));
        if (hasNext)
            return Format(Template(NextKey, NextEnglish), name, suffix, FormatDuration(nextInSeconds))
                + (derived ? DerivedMarker : "");
        return Format(Template(NotScheduledKey, NotScheduledEnglish), name, suffix, "");
    }

    /// <summary>The row a tier gets when this client is holding no usable packet.</summary>
    internal static string WaitingRow(RiftTier tier) =>
        Format(Template(WaitingKey, WaitingEnglish), TierName(tier), "", "");

    /// <summary>
    /// The two rows, T1 then T2, from a held packet. <paramref name="skewTicks"/> comes from
    /// <see cref="SkewTicks"/> at receive time, so the countdown is the server's schedule read on
    /// the client's clock.
    /// </summary>
    internal static IReadOnlyList<string> Rows(in RiftSchedule schedule, long localUtcNowTicks, long skewTicks)
    {
        return new[]
        {
            RowFor(schedule, RiftTier.Minor, localUtcNowTicks, skewTicks),
            RowFor(schedule, RiftTier.Major, localUtcNowTicks, skewTicks),
        };
    }

    /// <summary>The two rows shown before a packet arrives (or once one has gone stale).</summary>
    internal static IReadOnlyList<string> WaitingRows() =>
        new[] { WaitingRow(RiftTier.Minor), WaitingRow(RiftTier.Major) };

    /// <summary>One tier's row out of a packet.</summary>
    internal static string RowFor(in RiftSchedule s, RiftTier tier, long localUtcNowTicks, long skewTicks)
    {
        bool active = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorActive) != 0
            : (s.Flags & FlagMajorActive) != 0;
        long endTicks = tier == RiftTier.Minor ? s.MinorEndTicks : s.MajorEndTicks;
        long nextTicks = tier == RiftTier.Minor ? s.MinorNextTicks : s.MajorNextTicks;
        bool derived = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorNextDerived) != 0
            : (s.Flags & FlagMajorNextDerived) != 0;
        int level = tier == RiftTier.Minor ? s.MinorLevel : s.MajorLevel;

        double remaining = active && endTicks > 0 ? SecondsUntil(endTicks, localUtcNowTicks, skewTicks) : 0;
        double nextIn = nextTicks > 0 ? SecondsUntil(nextTicks, localUtcNowTicks, skewTicks) : 0;
        if (nextIn < 0) nextIn = 0;

        return Row(tier, level, active && remaining > 0, remaining, nextTicks > 0, nextIn, derived);
    }

    // ---- change detection and cadence -------------------------------------------------------

    /// <summary>
    /// Whether two snapshots differ in anything worth a packet. <c>UtcTicks</c> is deliberately
    /// excluded: it changes on every evaluation and comparing it would make every evaluation a
    /// "change", which is the heartbeat's job and at the heartbeat's rate.
    /// </summary>
    internal static bool Differs(in RiftSchedule a, in RiftSchedule b) =>
        a.MinorEndTicks != b.MinorEndTicks
        || a.MinorNextTicks != b.MinorNextTicks
        || a.MajorEndTicks != b.MajorEndTicks
        || a.MajorNextTicks != b.MajorNextTicks
        || a.MinorLevel != b.MinorLevel
        || a.MajorLevel != b.MajorLevel
        || a.NextType != b.NextType
        || a.Flags != b.Flags
        || a.Version != b.Version;

    /// <summary>
    /// The cadence rule, pure. First ever → send. Changed → send, but never closer together than
    /// <see cref="MinSendIntervalSeconds"/>. Otherwise the <see cref="HeartbeatSeconds"/>
    /// heartbeat. The floor deliberately applies to a change and not to the heartbeat: the
    /// heartbeat is already a minute apart.
    /// </summary>
    internal static SendReason Decide(bool everSent, bool changed, double secondsSinceLastSend)
    {
        if (!everSent) return SendReason.First;
        if (changed) return secondsSinceLastSend >= MinSendIntervalSeconds ? SendReason.Change : SendReason.None;
        return secondsSinceLastSend >= HeartbeatSeconds ? SendReason.Heartbeat : SendReason.None;
    }

    // ---- log lines --------------------------------------------------------------------------

    /// <summary>Compact one-line description of a snapshot, shared by the send and apply lines.</summary>
    internal static string Describe(in RiftSchedule s) =>
        "v" + N(s.Version)
        + " T1[" + TierState(s, RiftTier.Minor) + " level=" + N(s.MinorLevel) + "]"
        + " T2[" + TierState(s, RiftTier.Major) + " level=" + N(s.MajorLevel) + "]"
        + " nextType=" + NextTypeName(s.NextType)
        + " utc=" + N(s.UtcTicks);

    /// <summary><c>active end=…</c> / <c>next=… (derived)</c> / <c>none</c> for one tier.</summary>
    internal static string TierState(in RiftSchedule s, RiftTier tier)
    {
        bool active = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorActive) != 0
            : (s.Flags & FlagMajorActive) != 0;
        long endTicks = tier == RiftTier.Minor ? s.MinorEndTicks : s.MajorEndTicks;
        long nextTicks = tier == RiftTier.Minor ? s.MinorNextTicks : s.MajorNextTicks;
        bool derived = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorNextDerived) != 0
            : (s.Flags & FlagMajorNextDerived) != 0;

        if (active) return "active end=" + N(endTicks);
        if (nextTicks > 0) return "next=" + N(nextTicks) + (derived ? " (derived)" : " (scheduled)");
        return "none";
    }

    /// <summary><c>T1</c> / <c>T2</c> / <c>Primal</c> / <c>none</c> for a wire byte.</summary>
    internal static string NextTypeName(byte nextType) =>
        nextType == NextTypeNone ? "none"
        : nextType == (byte)RiftTier.Minor ? TierName(RiftTier.Minor)
        : nextType == (byte)RiftTier.Major ? TierName(RiftTier.Major)
        : nextType == (byte)RiftTier.Primal ? TierName(RiftTier.Primal)
        : "unknown(" + N(nextType) + ")";

    /// <summary>The one line per send. Grep <c>Sample rifts: sent</c>.</summary>
    internal static string SentLine(SendReason reason, string audience, int recipients, in RiftSchedule s) =>
        "sent SmRf to " + audience + " recipients=" + N(recipients)
        + " reason=" + reason.ToString().ToLowerInvariant() + " " + Describe(s);

    /// <summary>The one line per apply on the client. Grep <c>Sample rifts: applied</c>.</summary>
    internal static string AppliedLine(in RiftSchedule s, long skewTicks) =>
        "applied SmRf skew=" + Signed(skewTicks / (double)TimeSpan.TicksPerSecond) + "s " + Describe(s);

    /// <summary>The line for a packet this client refuses. One per refusal, naming why.</summary>
    internal static string RefusedLine(string why) => "ignored an SmRf packet: " + why;

    /// <summary>The line the fallback row sink writes when the rows change.</summary>
    internal static string RowsLine(IReadOnlyList<string> rows)
    {
        if (rows == null || rows.Count == 0) return "rows (none)";
        var sb = new System.Text.StringBuilder("rows ");
        for (int i = 0; i < rows.Count; i++)
        {
            if (i > 0) sb.Append(" | ");
            sb.Append('"').Append(rows[i]).Append('"');
        }
        return sb.ToString();
    }

    /// <summary>
    /// The line naming which row sink this client got, so a reader can tell "FUI36 is not in this
    /// Core" from "the rows are broken".
    /// </summary>
    internal static string SinkLine(string what) => "row sink = " + what;

    /// <summary>What <see cref="LogRowSink"/> calls itself in <see cref="SinkLine"/>.</summary>
    internal const string LogSinkName =
        "log fallback (the map attachment is disabled: server, skipped Ui module, or unknown site)";

    // ---- .rifts -----------------------------------------------------------------------------

    /// <summary>What <c>.rifts</c> was asked for.</summary>
    internal enum Kind
    {
        /// <summary>No argument: print the schedule.</summary>
        Status = 0,

        /// <summary>Re-send the current schedule to the caller's own client.</summary>
        Send = 1,
    }

    /// <summary>Parse <c>.rifts</c> / <c>.rifts send</c>. Anything else is refused, not guessed.</summary>
    internal static bool TryParse(string? token, out Kind kind)
    {
        kind = Kind.Status;
        if (string.IsNullOrWhiteSpace(token)) return true;
        string head = token!.Trim().ToLowerInvariant();
        if (head == "status") return true;
        if (head != "send") return false;
        kind = Kind.Send;
        return true;
    }

    /// <summary>Usage line, replied on anything <see cref="TryParse"/> refuses.</summary>
    internal static string UsageReply => "Usage: .rifts [status] | .rifts send";

    /// <summary>
    /// The whole <c>.rifts</c> reply, pure. Two rows in the same shape the client renders — that
    /// is the point of the command: the server half is provable with no client at all — then the
    /// raw ticks, an explicit sentence about which number is scheduled and which is estimated, and
    /// the broadcast age.
    /// </summary>
    internal static IReadOnlyList<string> StatusLines(
        bool hasData, in RiftSchedule s, long nowUtcTicks, bool everSent,
        double secondsSinceLastSend, int lastRecipients)
    {
        var lines = new List<string>(6);
        if (!hasData)
        {
            lines.Add("No rift schedule has been read yet on this server (WarEventSystem not available, or the first evaluation has not run).");
            return lines;
        }

        // The server's own clock is the reference, so skew is zero by construction here.
        lines.Add(RowFor(s, RiftTier.Minor, nowUtcTicks, 0));
        lines.Add(RowFor(s, RiftTier.Major, nowUtcTicks, 0));
        lines.Add("raw: T1 end=" + N(s.MinorEndTicks) + " next=" + N(s.MinorNextTicks)
                  + "; T2 end=" + N(s.MajorEndTicks) + " next=" + N(s.MajorNextTicks)
                  + "; serverUtc=" + N(s.UtcTicks));
        lines.Add(Provenance(s));
        lines.Add(everSent
            ? "last broadcast " + FormatDuration(secondsSinceLastSend) + " ago to " + N(lastRecipients) + " user(s); heartbeat every " + N((int)HeartbeatSeconds) + "s."
            : "never broadcast this Core generation.");
        return lines;
    }

    /// <summary>
    /// The sentence that keeps a derivation honest in words rather than in a tilde. Named
    /// separately so a test can assert that an estimated tier is *always* called estimated.
    /// </summary>
    internal static string Provenance(in RiftSchedule s)
    {
        var scheduled = new List<string>(2);
        var estimated = new List<string>(2);
        var unknown = new List<string>(2);
        Classify(s, RiftTier.Minor, scheduled, estimated, unknown);
        Classify(s, RiftTier.Major, scheduled, estimated, unknown);

        string text = "the scheduler's next event is " + NextTypeName(s.NextType) + ".";
        if (scheduled.Count > 0) text += " Scheduled: " + string.Join(", ", scheduled) + ".";
        if (estimated.Count > 0)
            text += " Estimated (duration + gap, ignores the daily window, not a schedule): "
                    + string.Join(", ", estimated) + ".";
        if (unknown.Count > 0) text += " No data: " + string.Join(", ", unknown) + ".";
        return text;
    }

    private static void Classify(
        in RiftSchedule s, RiftTier tier, List<string> scheduled, List<string> estimated, List<string> unknown)
    {
        bool active = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorActive) != 0
            : (s.Flags & FlagMajorActive) != 0;
        if (active) return;   // an active tier is reporting its own end tick, not a prediction
        long next = tier == RiftTier.Minor ? s.MinorNextTicks : s.MajorNextTicks;
        bool derived = tier == RiftTier.Minor
            ? (s.Flags & FlagMinorNextDerived) != 0
            : (s.Flags & FlagMajorNextDerived) != 0;
        if (next <= 0) unknown.Add(TierName(tier));
        else if (derived) estimated.Add(TierName(tier));
        else scheduled.Add(TierName(tier));
    }

    // ---- server state -----------------------------------------------------------------------
    //
    // Game-free so the cadence and the handshake-edge bookkeeping are unit tested rather than
    // eyeballed on a live server. Nothing here names a game type; the caller passes indices in.

    private static readonly object Gate = new();
    private static RiftSchedule _lastSent;
    private static bool _everSent;
    private static long _lastSendTicks;
    private static int _lastRecipients;
    private static RiftSchedule _latest;
    private static bool _hasLatest;
    private static readonly HashSet<int> HandshakeOkUsers = new();

    // client
    private static RiftSchedule _held;
    private static bool _hasHeld;
    private static long _heldReceiveTicks;
    private static long _heldSkewTicks;

    /// <summary>The most recent snapshot the server built, and whether there is one.</summary>
    internal static bool TryLatest(out RiftSchedule schedule)
    {
        lock (Gate)
        {
            schedule = _latest;
            return _hasLatest;
        }
    }

    /// <summary>Record a freshly built snapshot without deciding anything about sending it.</summary>
    internal static void NoteLatest(in RiftSchedule schedule)
    {
        lock (Gate)
        {
            _latest = schedule;
            _hasLatest = true;
        }
    }

    /// <summary>
    /// The cadence decision against this generation's send history.
    /// <paramref name="nowUtcTicks"/> is the server's clock; the elapsed time is computed here so
    /// the caller cannot get it wrong.
    /// </summary>
    internal static SendReason DecideSend(in RiftSchedule candidate, long nowUtcTicks)
    {
        lock (Gate)
        {
            double since = _everSent
                ? (nowUtcTicks - _lastSendTicks) / (double)TimeSpan.TicksPerSecond
                : 0;
            bool changed = _everSent && Differs(candidate, _lastSent);
            return Decide(_everSent, changed, since);
        }
    }

    /// <summary>Record a broadcast: what went out, when, and to how many.</summary>
    internal static void NoteSent(in RiftSchedule sent, long nowUtcTicks, int recipients)
    {
        lock (Gate)
        {
            _lastSent = sent;
            _everSent = true;
            _lastSendTicks = nowUtcTicks;
            _lastRecipients = recipients;
        }
    }

    /// <summary>Whether anything has been broadcast this Core generation.</summary>
    internal static bool EverSent
    {
        get { lock (Gate) return _everSent; }
    }

    /// <summary>Recipients of the last broadcast. 0 when nothing has gone out.</summary>
    internal static int LastRecipients
    {
        get { lock (Gate) return _lastRecipients; }
    }

    /// <summary>Seconds since the last broadcast, or 0 when there has not been one.</summary>
    internal static double SecondsSinceLastSend(long nowUtcTicks)
    {
        lock (Gate)
        {
            if (!_everSent) return 0;
            return (nowUtcTicks - _lastSendTicks) / (double)TimeSpan.TicksPerSecond;
        }
    }

    /// <summary>
    /// The handshake-ok edge for one user: true exactly once per user per transition into
    /// <c>ok</c>. The join send rides this rather than <c>PlayerConnected</c>, because a user is
    /// connected before they have answered the handshake and this plugin is
    /// <c>ServerMustHave</c> — sending to a peer that is about to be kicked is noise. Mechanism
    /// mirrors Core's own <c>PrefabEditWireApply.PollHandshakeResends</c>.
    /// </summary>
    internal static bool NoteHandshakeOk(int userIndex, bool isConnected, bool answerIsOk)
    {
        lock (Gate)
        {
            if (!isConnected || !answerIsOk)
            {
                // Not ok, or not ok any more: arm the edge so the next ok re-sends.
                HandshakeOkUsers.Remove(userIndex);
                return false;
            }

            return HandshakeOkUsers.Add(userIndex);
        }
    }

    /// <summary>
    /// Drop every user index that is no longer connected. <c>User.Index</c> is a connection slot
    /// the game recycles, so a row that outlives its owner suppresses the join send for the next
    /// player to land in that slot — the same defect <c>SampleNet.ForgetUser</c> exists for.
    /// </summary>
    internal static void RetainUsers(IReadOnlyCollection<int> connected)
    {
        lock (Gate)
        {
            if (connected == null || connected.Count == 0)
            {
                HandshakeOkUsers.Clear();
                return;
            }

            List<int>? drop = null;
            foreach (int index in HandshakeOkUsers)
            {
                bool keep = false;
                foreach (int live in connected)
                {
                    if (live != index) continue;
                    keep = true;
                    break;
                }

                if (!keep) (drop ??= new List<int>()).Add(index);
            }

            if (drop == null) return;
            for (int i = 0; i < drop.Count; i++) HandshakeOkUsers.Remove(drop[i]);
        }
    }

    /// <summary>How many connection slots are currently marked handshake-ok. Diagnostics and tests.</summary>
    internal static int TrackedUsers
    {
        get { lock (Gate) return HandshakeOkUsers.Count; }
    }

    // ---- client state -----------------------------------------------------------------------

    /// <summary>
    /// Hold a received packet with the local clock reading at receive, which fixes the skew for
    /// this packet. Returns the skew so the caller can log it.
    /// </summary>
    internal static long HoldSchedule(in RiftSchedule schedule, long localUtcTicksAtReceive)
    {
        long skew = SkewTicks(schedule.UtcTicks, localUtcTicksAtReceive);
        lock (Gate)
        {
            _held = schedule;
            _hasHeld = true;
            _heldReceiveTicks = localUtcTicksAtReceive;
            _heldSkewTicks = skew;
        }
        return skew;
    }

    /// <summary>
    /// Forget the held packet. The client does this on <c>WorldReady</c>, so a reconnect to a
    /// different server cannot show the previous server's rifts — the same discipline
    /// <c>SampleNet.ForgetUser</c> applies to its per-session table.
    /// </summary>
    internal static bool ForgetSchedule()
    {
        lock (Gate)
        {
            bool had = _hasHeld;
            _hasHeld = false;
            _held = default;
            _heldReceiveTicks = 0;
            _heldSkewTicks = 0;
            return had;
        }
    }

    /// <summary>Whether this client is holding a packet at all (stale or not).</summary>
    internal static bool HasSchedule
    {
        get { lock (Gate) return _hasHeld; }
    }

    /// <summary>
    /// The rows to show right now: the held packet's countdowns, or the waiting rows when there is
    /// no packet or the one held has gone stale.
    /// </summary>
    internal static IReadOnlyList<string> CurrentRows(long localUtcNowTicks)
    {
        RiftSchedule held;
        long receive, skew;
        lock (Gate)
        {
            if (!_hasHeld) return WaitingRows();
            held = _held;
            receive = _heldReceiveTicks;
            skew = _heldSkewTicks;
        }

        if (IsStale(receive, localUtcNowTicks)) return WaitingRows();
        return Rows(held, localUtcNowTicks, skew);
    }

    /// <summary>Forget every server and client counter. Attach and unload both do this.</summary>
    internal static void Reset()
    {
        lock (Gate)
        {
            _lastSent = default;
            _everSent = false;
            _lastSendTicks = 0;
            _lastRecipients = 0;
            _latest = default;
            _hasLatest = false;
            HandshakeOkUsers.Clear();
            _held = default;
            _hasHeld = false;
            _heldReceiveTicks = 0;
            _heldSkewTicks = 0;
        }
    }

    // ---- the row sink -----------------------------------------------------------------------

    /// <summary>
    /// Where the two client rows go. One method, because that is the shape job FUI36's
    /// <c>IVanillaAttachment</c> handle exposes (<c>SetRows(IReadOnlyList&lt;string&gt;)</c>);
    /// keeping the seam this narrow is what makes swapping the fallback for the real attachment a
    /// two-line change in <see cref="CreateRowSink"/>.
    /// </summary>
    internal interface IRiftRowSink : IDisposable
    {
        /// <summary>Show these rows. Called at <see cref="ClientRefreshFrames"/>; must be cheap and idempotent.</summary>
        void SetRows(IReadOnlyList<string> rows);

        /// <summary>What this sink is, for the one <see cref="SinkLine"/> at attach.</summary>
        string Name { get; }
    }

    /// <summary>
    /// A sink that writes one line whenever the rows actually change, which is at most once a
    /// second — so the client half of this feature is provable from the log with no UI at all, and
    /// so the log does not fill up at 4 Hz with a string that did not move. Used on its own when
    /// the attachment handle is disabled (server, skipped Ui module, unknown site), and mirrored
    /// alongside the real attachment is deliberately <em>not</em> done: one line a second into a
    /// player's client log is a cost, and a working attachment is its own evidence.
    /// </summary>
    internal sealed class LogRowSink : IRiftRowSink
    {
        private string _last = "";

        /// <inheritdoc />
        public string Name => LogSinkName;

        /// <inheritdoc />
        public void SetRows(IReadOnlyList<string> rows)
        {
            string line = RowsLine(rows);
            if (string.Equals(line, _last, StringComparison.Ordinal)) return;
            _last = line;
            SampleLog.Info(Area, line);
        }

        /// <inheritdoc />
        public void Dispose() => _last = "";
    }

    private static IRiftRowSink? _sink;

    /// <summary>The row sink currently installed on this client, if any. Tests substitute one.</summary>
    internal static IRiftRowSink? Sink
    {
        get { lock (Gate) return _sink; }
    }

    /// <summary>Install a sink, disposing whatever was there. Returns the installed sink.</summary>
    internal static IRiftRowSink InstallSink(IRiftRowSink sink)
    {
        if (sink == null) throw new ArgumentNullException(nameof(sink));
        IRiftRowSink? previous;
        lock (Gate)
        {
            previous = _sink;
            _sink = sink;
        }

        try { previous?.Dispose(); }
        catch { /* a broken sink must not stop the new one being installed */ }
        return sink;
    }

    /// <summary>Remove and dispose the installed sink. True when there was one.</summary>
    internal static bool RemoveSink()
    {
        IRiftRowSink? previous;
        lock (Gate)
        {
            previous = _sink;
            _sink = null;
        }

        if (previous == null) return false;
        try { previous.Dispose(); }
        catch { /* unload must not throw out of a sink */ }
        return true;
    }

    /// <summary>Push rows to the installed sink. Returns false when there is nothing to push to.</summary>
    internal static bool PushRows(IReadOnlyList<string> rows)
    {
        IRiftRowSink? sink;
        lock (Gate) sink = _sink;
        if (sink == null) return false;
        sink.SetRows(rows);
        return true;
    }

    private static string N(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string N(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Signed(double seconds) =>
        (seconds >= 0 ? "+" : "") + seconds.ToString("0.###", CultureInfo.InvariantCulture);

#if !PROGENITOR_TESTS
    /// <summary>
    /// Registration for both sides. The server reads the scheduler and sends; the client receives,
    /// renders and clears on a new world. Every registration comes from <c>ctx</c>, so unload drops
    /// the handler, the subscriptions and the virtual systems.
    /// </summary>
    internal static unsafe void Attach(PluginContext ctx)
    {
        if (ctx == null) throw new ArgumentNullException(nameof(ctx));
        Reset();
        TemplateResolver = ResolveTemplate;

        try
        {
            if (ctx.IsServer)
            {
                ctx.Systems.Add(ServerSystemName, OnServerTick);
                SampleLog.Info(Area, "server reading WarEventSystem every " + N(ServerEvaluateFrames)
                    + " frame(s); broadcasting " + Describe(ScheduleEventId)
                    + " on join, on change, and every " + N((int)HeartbeatSeconds) + "s.");
                return;
            }

            ctx.Net.OnReceive(ScheduleEventId, OnScheduleFromServer);
            // A client world teardown must not leave the previous server's rifts on screen.
            ctx.Events.On<WorldReady>(OnClientWorldReady);
            ctx.Systems.Add(ClientSystemName, OnClientTick);
            var sink = InstallSink(CreateRowSink(ctx));
            SampleLog.Info(Area, "client listening for " + Describe(ScheduleEventId)
                + "; rows refreshed every " + N(ClientRefreshFrames) + " frame(s). " + SinkLine(sink.Name));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts attach threw: " + e);
        }
    }

    /// <summary>
    /// Job FEX9 (R60 finding 9). One row template out of the game's string table, or null.
    /// The keys live in this plugin's <c>localization/en.json</c>, which Core auto-loads before
    /// <c>OnLoad</c> and (on the client) writes into <c>Localization.LocalizedStrings</c>; the
    /// server records the string but has no table, so this is null there and the English constant
    /// stands — which is the right answer for the <c>.rifts</c> reply anyway, a chat line the
    /// server writes in its own language.
    /// </summary>
    private static string? ResolveTemplate(string key)
    {
        try
        {
            return LocalizationApi.TryGetGameString(LocalizationApi.KeyFor(key), out string text)
                && !string.IsNullOrEmpty(text)
                ? text
                : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// The one place that knows where the client rows go: two rows hosted beside the vanilla
    /// Rift Incursions card on the world map. A failure to attach is not a failure here — the
    /// handle comes back disabled-but-valid and <see cref="VanillaAttachRowSink"/> falls back to
    /// the log, which is what keeps this feature demonstrable on a client whose Ui module was
    /// skipped.
    /// </summary>
    private static IRiftRowSink CreateRowSink(PluginContext ctx)
    {
        var handle = Progenitor.Ui.Ui.AttachToVanilla(VanillaUiSite.MapWarEventInfo, ctx.Owner, ctx.Guid);
        return new VanillaAttachRowSink(handle);
    }

    /// <summary>
    /// The real sink: <c>IVanillaAttachHandle.SetRows</c>, which grows or shrinks the row list to
    /// match so a refresh cannot leave a stale row behind. Disposing it destroys the container —
    /// and disposing it is not optional, because that container is parented into the vanilla map
    /// menu and is therefore the one thing Core's DontDestroyOnLoad prefix sweep cannot reach
    /// (research §4.4).
    /// </summary>
    internal sealed class VanillaAttachRowSink : IRiftRowSink
    {
        private readonly IVanillaAttachHandle _handle;
        private readonly LogRowSink? _fallback;

        /// <summary>Wrap an attachment handle. Never given a null one: <c>AttachToVanilla</c> cannot return one.</summary>
        internal VanillaAttachRowSink(IVanillaAttachHandle handle)
        {
            _handle = handle ?? throw new ArgumentNullException(nameof(handle));
            _fallback = handle.Enabled ? null : new LogRowSink();
        }

        /// <inheritdoc />
        public string Name => _handle.Enabled
            ? "Ui.AttachToVanilla(" + _handle.Site + ") as " + _handle.ContainerName
            : LogSinkName;

        /// <inheritdoc />
        public void SetRows(IReadOnlyList<string> rows)
        {
            if (_fallback != null)
            {
                _fallback.SetRows(rows);
                return;
            }

            _handle.SetRows(rows);
        }

        /// <inheritdoc />
        public void Dispose()
        {
            try { _handle.Dispose(); }
            catch (Exception e) { Plugin.Logger?.LogWarning("Sample rifts: disposing the map attachment threw: " + e.Message); }
            _fallback?.Dispose();
        }
    }

    private static string Describe(int eventId) =>
        "event id 0x" + eventId.ToString("X8", CultureInfo.InvariantCulture);

    // ---- server -------------------------------------------------------------------------------

    /// <summary>
    /// One evaluation: read the scheduler, build the packet, send it to any user who has just
    /// finished the handshake, and broadcast on change or on the heartbeat.
    /// </summary>
    private static void OnServerTick(VirtualSystemContext vs)
    {
        if (vs.Frame % ServerEvaluateFrames != 0) return;
        try
        {
            var plugin = Plugin.Active;
            if (plugin == null) return;

            long now = DateTime.UtcNow.Ticks;
            if (!TryReadSchedule(vs.World, now, out var schedule)) return;
            NoteLatest(schedule);

            SendJoinPackets(plugin, vs.World, schedule);

            var reason = DecideSend(schedule, now);
            if (reason == SendReason.None) return;

            int recipients = plugin.Net.SendToAll(vs.World, ScheduleEventId, schedule);
            NoteSent(schedule, now, recipients);
            SampleLog.Info(Area, SentLine(reason, "all", recipients, schedule));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts server tick threw: " + e);
        }
    }

    /// <summary>
    /// Read <c>WarEventSystem</c> and the two war-event component types. Every read here is a
    /// managed public field or an ordinary query; nothing raw, nothing written.
    /// </summary>
    internal static bool TryReadSchedule(World? world, long nowUtcTicks, out RiftSchedule schedule)
    {
        schedule = default;
        if (world == null || !world.IsCreated) return false;

        WarEventSystem? system;
        try { system = world.GetExistingSystemManaged<WarEventSystem>(); }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample rifts: GetExistingSystemManaged<WarEventSystem> threw: " + e.Message);
            return false;
        }

        if (system == null) return false;

        var scheduling = system.SchedulingData;
        var spawn = system.SpawnSettings;

        double minorDuration = 0, majorDuration = 0, minorGap = 0, majorGap = 0;
        try
        {
            // Durations and intervals are stored as enums; the game's accessors map them to
            // seconds. Never read the enum as a number (research §2.1).
            minorDuration = spawn.GetTotalDuration(WarEventType.Minor);
            majorDuration = spawn.GetTotalDuration(WarEventType.Major);
            minorGap = spawn.GetTotalTimeBetweenEvents(WarEventType.Minor);
            majorGap = spawn.GetTotalTimeBetweenEvents(WarEventType.Major);
        }
        catch (Exception e)
        {
            // A failed accessor means no estimate, which means "not scheduled" - not a guess.
            Plugin.Logger?.LogWarning("Sample rifts: WarEventGameSettings accessor threw: " + e.Message);
        }

        ReadLevels(world, out int minorLevel, out int majorLevel);

        var minor = TierReading.LevelOnly(minorLevel);
        var major = TierReading.LevelOnly(majorLevel);
        ReadActiveEvents(world, nowUtcTicks, minorLevel, majorLevel, ref minor, ref major);

        var nextType = (RiftTier)(byte)scheduling.NextWarEventType;
        schedule = Build(
            nowUtcTicks, minor, major,
            hasScheduling: scheduling.NextEventSpawnTicks > 0,
            scheduling.NextEventSpawnTicks,
            nextType,
            minorDuration, majorDuration, minorGap, majorGap);
        return true;
    }

    /// <summary>
    /// The live <c>WarEvent</c> entities, bucketed by tier. Disabled entities are included: a
    /// default query skips them (interop trap 4) and whether a war-event entity can be disabled is
    /// an open question in the research, so the cheap safe answer is taken.
    /// </summary>
    private static void ReadActiveEvents(
        World world, long nowUtcTicks, int minorLevel, int majorLevel,
        ref TierReading minor, ref TierReading major)
    {
        try
        {
            var em = world.EntityManager;
            var events = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<WarEvent>(ComponentType.AccessMode.ReadOnly));

            for (int i = 0; i < events.Count; i++)
            {
                if (!events[i].TryRead<WarEvent>(em, out var ev)) continue;
                var reading = new TierReading(
                    true, ev.StartTimeTicks, ev.EndTimeTicks,
                    ev.EventType == WarEventType.Minor ? minorLevel : majorLevel);

                if (ev.EventType == WarEventType.Minor) minor = PickBetter(minor, reading, nowUtcTicks);
                else if (ev.EventType == WarEventType.Major) major = PickBetter(major, reading, nowUtcTicks);
                // Primal is deliberately not carried in v1.
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample rifts: reading WarEvent entities threw: " + e.Message);
        }
    }

    /// <summary>
    /// Gear levels from <c>WarEventSettingsComponent</c>: recommended, else minimum, else 0. Zero
    /// means "unknown" and the row omits the suffix — it never falls back to a hardcoded 57 / 80,
    /// because a number that is
    /// wrong on somebody else's server is worse than no number.
    /// </summary>
    private static void ReadLevels(World world, out int minorLevel, out int majorLevel)
    {
        minorLevel = 0;
        majorLevel = 0;
        try
        {
            var em = world.EntityManager;
            var found = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<WarEventSettingsComponent>(ComponentType.AccessMode.ReadOnly));
            if (found.Count == 0) return;
            if (!found[0].TryRead<WarEventSettingsComponent>(em, out var settings)) return;
            minorLevel = PickLevel(settings.MinorWarEventSettings);
            majorLevel = PickLevel(settings.MajorWarEventSettings);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Sample rifts: reading WarEventSettingsComponent threw: " + e.Message);
        }
    }

    private static int PickLevel(WarEventTypeSettings settings)
    {
        if (settings.RecommendedGearLevel > 0) return settings.RecommendedGearLevel;
        return settings.MinGearLevel > 0 ? settings.MinGearLevel : 0;
    }

    /// <summary>
    /// Send the schedule to every user who has just reached handshake-ok, and scrub the rows of
    /// users who have left. Polled rather than subscribed for the same reason Core's own
    /// <c>PrefabEditWireApply.PollHandshakeResends</c> polls: the seam is a public status token,
    /// not an event.
    /// </summary>
    private static void SendJoinPackets(SampleContext plugin, World world, in RiftSchedule schedule)
    {
        try
        {
            var em = world.EntityManager;
            var users = Query.Entities(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<User>(ComponentType.AccessMode.ReadOnly));

            var connected = new List<int>();
            for (int i = 0; i < users.Count; i++)
            {
                User user;
                try { user = em.GetComponentData<User>(users[i]); }
                catch { continue; }

                bool ok = user.IsConnected && string.Equals(
                    Handshake.UserAnswerStatus(user.Index), HandshakeOkToken, StringComparison.Ordinal);
                if (user.IsConnected) connected.Add(user.Index);
                if (!NoteHandshakeOk(user.Index, user.IsConnected, ok)) continue;

                bool sent = plugin.Net.SendToUser(world, users[i], ScheduleEventId, schedule);
                SampleLog.Info(Area, SentLine(SendReason.First, "user " + user.Index, sent ? 1 : 0, schedule));
            }

            RetainUsers(connected);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts join send threw: " + e);
        }
    }

    /// <summary>Server entry point for <c>.rifts</c>.</summary>
    internal static void HandleCommand(ICommandContext ctx, Kind kind)
    {
        long now = DateTime.UtcNow.Ticks;

        // A fresh read, so the reply is what the scheduler says now rather than what the last
        // evaluation happened to catch. Falls back to the held snapshot if the read fails.
        if (TryReadSchedule(ctx.World, now, out var fresh)) NoteLatest(fresh);
        bool hasData = TryLatest(out var schedule);

        if (kind == Kind.Send)
        {
            if (!hasData)
            {
                ctx.Reply("No rift schedule to send yet.");
                return;
            }

            var plugin = Plugin.Active;
            if (plugin == null)
            {
                ctx.Reply("Sample plugin context is not available.");
                return;
            }

            // Deliberately SendToUser, not SendToAll: this command is an ungated read, and a
            // player-triggerable broadcast to everybody is a spam surface that an ungated command
            // must not open. Re-pushing the schedule to your own client is the useful half.
            bool sent = plugin.Net.SendToUser(ctx.UserEntity, ScheduleEventId, schedule);
            SampleLog.Info(Area, SentLine(SendReason.Change, "user " + ctx.User.Index, sent ? 1 : 0, schedule));
            ctx.Reply(sent
                ? "Rift schedule re-sent to your client."
                : "Could not send the rift schedule to your client (are you connected?).");
            return;
        }

        foreach (var line in StatusLines(
                     hasData, schedule, now, EverSent, SecondsSinceLastSend(now), LastRecipients))
            ctx.Reply(line);
    }

    // ---- client -------------------------------------------------------------------------------

    /// <summary>
    /// Client receive. Refuses anything it cannot read and anything from a protocol version it
    /// does not know, and says which — a silent no-op here is a player staring at an empty card.
    /// </summary>
    private static unsafe void OnScheduleFromServer(int eventId, byte* payload, int length)
    {
        try
        {
            if (eventId != ScheduleEventId) return;
            if (!IsPayloadLength(length) || !CustomEvents.TryRead<RiftSchedule>(payload, length, out var schedule))
            {
                SampleLog.Info(Area, RefusedLine("payload was " + N(length) + " byte(s), not one RiftSchedule ("
                    + N(PayloadBytes) + ")"));
                return;
            }

            if (!IsKnownVersion(schedule.Version))
            {
                SampleLog.Info(Area, RefusedLine("wire version " + N(schedule.Version) + ", this client reads "
                    + N(ProtocolVersion)));
                return;
            }

            long skew = HoldSchedule(schedule, DateTime.UtcNow.Ticks);
            SampleLog.Info(Area, AppliedLine(schedule, skew));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts client receive threw: " + e);
        }
    }

    /// <summary>Push the current rows at <see cref="ClientRefreshFrames"/>.</summary>
    private static void OnClientTick(VirtualSystemContext vs)
    {
        if (vs.Frame % ClientRefreshFrames != 0) return;
        try { PushRows(CurrentRows(DateTime.UtcNow.Ticks)); }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts client tick threw: " + e);
        }
    }

    /// <summary>
    /// A new client world means a new (or re-entered) server. Forget the held packet rather than
    /// counting down from the previous server's schedule until the first heartbeat arrives.
    /// </summary>
    private static void OnClientWorldReady(WorldReady ev)
    {
        try
        {
            if (!ForgetSchedule()) return;
            SampleLog.Info(Area, "forgot the held schedule on a new world (" + ev.World.Name
                + "): a reconnect must not show the previous server's rifts.");
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError("Sample rifts world-ready handler threw: " + e);
        }
    }
#endif

    /// <summary>
    /// Unload. Core drops the receive handler, the <c>WorldReady</c> subscription and both virtual
    /// systems (all tracked on the context); the held packet, the send history and the row sink are
    /// this generation's and must not outlive it — a kept sink would be a live writer into a
    /// container the next generation does not own.
    /// </summary>
    internal static void Detach()
    {
        // Job FEX9: the resolver closes over nothing, but it is a delegate to a method in this
        // assembly, so it goes with the generation like the sink does.
        TemplateResolver = null;
        RemoveSink();
        Reset();
    }
}
