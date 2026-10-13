using System;
using System.Collections.Generic;
using System.Text;

namespace Progenitor.Debugging;

/// <summary>
/// Game-free decisions behind <c>.debug build on|off</c>, the admin sandbox build mode: which of the
/// game's own server debug settings it switches, what an <c>on</c> or <c>off</c> writes given what
/// is set now, the per-save record of what it changed, and the readout lines. The state machine
/// that applies them is <see cref="DebugBuildController"/>; the interop half
/// (<c>DebugBuildRuntime</c>) reads and pokes the bytes.
///
/// <para><b>Server-wide.</b> These are the game's <c>SyncedServerDebugSettings</c>, one networked
/// singleton replicated to every client. While build mode is on, every player on the server builds
/// without costs and with free placement, not only the admin who turned it on.</para>
///
/// <para><b>Exact restore.</b> <c>on</c> sets only the settings that read false and records exactly
/// those names. <c>off</c> clears only recorded names that still read true, then drops the record.
/// A setting that was already on before is never recorded and never cleared, unless there is no
/// readable record at all: then <c>off</c> clears all of them and says so.</para>
/// </summary>
internal static class DebugBuildMode
{
    /// <summary>One switched setting: the game's field name and its byte offset in <c>SyncedServerDebugSettings</c>.</summary>
    internal readonly record struct Setting(string Name, int Offset, string Plain);

    /// <summary>
    /// The settings build mode turns on, in the singleton's field order. Offsets are the
    /// <c>layout.txt</c> facts, checked against the dump by <c>tools/layoutgen --check</c>.
    /// Deliberately left out: <c>CastleHeartBloodEssenceDisabled</c> (castle upkeep and decay for
    /// every castle on the server, not building), <c>BuildingPlacementRestrictionsDisabled</c> (see
    /// <see cref="Retired"/>) and the non-build switches such as <c>BuildingReplacementDisabled</c>
    /// or <c>GlobalCastleTerritoryEnabled</c>.
    /// </summary>
    internal static readonly IReadOnlyList<Setting> Settings = new[]
    {
        new Setting("BuildCostsDisabled", Layout.SyncedServerDebugSettingsBuildCostsDisabledOffset, "no build costs"),
        new Setting("AllProgressionUnlocked", Layout.SyncedServerDebugSettingsAllProgressionUnlockedOffset, "every recipe and structure unlocked"),
        new Setting("FreeBuildingPlacementEnabled", Layout.SyncedServerDebugSettingsFreeBuildingPlacementEnabledOffset, "free placement"),
        new Setting("FloorPlacementRestrictionsDisabled", Layout.SyncedServerDebugSettingsFloorPlacementRestrictionsDisabledOffset, "no floor restrictions"),
        new Setting("CastleLimitsDisabled", Layout.SyncedServerDebugSettingsCastleLimitsDisabledOffset, "no castle limits"),
        new Setting("CastleDragBuildingEnabled", Layout.SyncedServerDebugSettingsCastleDragBuildingEnabledOffset, "drag building"),
        new Setting("BuildingProximityRequirementDisabled", Layout.SyncedServerDebugSettingsBuildingProximityRequirementDisabledOffset, "no proximity requirement"),
    };

    /// <summary>
    /// Settings an earlier build mode turned on and this one never does. <c>on</c> does not write
    /// them, and clears them only when a record names them (a record written before the change):
    /// <c>off</c>, the after-load restore, an <c>on</c> over such a record and an adopt. Without a
    /// record naming it, a retired setting is an operator's own and is never cleared, not even by
    /// <c>off</c> with no readable record; status and <c>on</c> warn instead.
    /// <c>BuildingPlacementRestrictionsDisabled</c> makes the game skip the territory check that
    /// also picks the castle heart a new piece joins, so everything built with it on belongs to no
    /// castle (empty <c>CastleHeartConnection</c>, the prefab's team): its owner could not hit it in
    /// testing ("Cannot damage an active enemy castle") and it is not part of the castle.
    /// <see cref="DetachedPieces"/> finds pieces already built that way.
    /// </summary>
    internal static readonly IReadOnlyList<Setting> Retired = new[]
    {
        new Setting("BuildingPlacementRestrictionsDisabled", Layout.SyncedServerDebugSettingsBuildingPlacementRestrictionsDisabledOffset, "no placement restrictions (pieces join no castle)"),
    };

    /// <summary>The last byte any setting occupies, plus one: the extent every read and write asks the access guard for.</summary>
    internal static int Extent
    {
        get
        {
            int max = 0;
            foreach (var s in Tracked()) max = Math.Max(max, s.Offset + 1);
            return max;
        }
    }

    /// <summary>Every setting <c>on</c> switches, in table order.</summary>
    internal static List<string> AllNames()
    {
        var names = new List<string>();
        foreach (var s in Settings) names.Add(s.Name);
        return names;
    }

    /// <summary>The switched settings, then the <see cref="Retired"/> ones: everything read, and everything <c>off</c> may clear.</summary>
    internal static IEnumerable<Setting> Tracked()
    {
        foreach (var s in Settings) yield return s;
        foreach (var s in Retired) yield return s;
    }

    /// <summary>True for a name in <see cref="Retired"/>.</summary>
    internal static bool IsRetired(string name)
    {
        foreach (var s in Retired)
            if (s.Name == name) return true;
        return false;
    }

    // ---- the record ----------------------------------------------------------------------------

    /// <summary>ModSave key of the record, in Core's <c>progenitor.debug</c> store (beside the debug buff's ledger).</summary>
    internal const string MarkerKey = "debug.build";

    private const string MarkerPrefix = "on;";
    private const string ProcessField = "p=";

    /// <summary>
    /// A decoded record. <see cref="Names"/> holds every name it lists, known settings first in
    /// table order, then names this build does not know (kept so a later list change can never
    /// strand a setting on). <see cref="ProcessStamp"/> identifies the server process that last
    /// turned the mode on or adopted it; empty in a record written before the stamp existed.
    /// </summary>
    internal sealed record Record(List<string> Names, string ProcessStamp);

    /// <summary>
    /// The record: <c>on;p=&lt;process&gt;;</c> followed by the comma-separated names build mode
    /// turned on (possibly none).
    /// </summary>
    internal static string EncodeMarker(IEnumerable<string> turnedOn, string processStamp)
    {
        var sb = new StringBuilder(MarkerPrefix);
        sb.Append(ProcessField).Append(Clean(processStamp)).Append(';');
        bool first = true;
        foreach (var name in turnedOn)
        {
            if (!first) sb.Append(',');
            sb.Append(name);
            first = false;
        }
        return sb.ToString();
    }

    /// <summary>
    /// A record from its stored text. Null when the text is not one of ours (null, empty or any
    /// other shape): the caller decides whether that is "no record" or "unreadable". Also reads the
    /// first format, <c>on;</c> plus names with no process stamp.
    /// </summary>
    internal static Record? DecodeMarker(string? marker)
    {
        if (marker == null || !marker.StartsWith(MarkerPrefix, StringComparison.Ordinal)) return null;
        string rest = marker.Substring(MarkerPrefix.Length);
        string stamp = "";
        if (rest.StartsWith(ProcessField, StringComparison.Ordinal))
        {
            int end = rest.IndexOf(';');
            if (end < 0) return null;
            stamp = rest.Substring(ProcessField.Length, end - ProcessField.Length);
            rest = rest.Substring(end + 1);
        }
        var named = new List<string>();
        foreach (var part in rest.Split(','))
        {
            string name = part.Trim();
            if (name.Length > 0 && !named.Contains(name)) named.Add(name);
        }
        var ordered = new List<string>();
        foreach (var s in Tracked())
            if (named.Contains(s.Name)) ordered.Add(s.Name);
        foreach (var name in named)
            if (OffsetOf(name) < 0) ordered.Add(name);
        return new Record(ordered, stamp);
    }

    private static string Clean(string? stamp)
    {
        var sb = new StringBuilder();
        foreach (char c in stamp ?? "")
            if (c != ';' && c != ',') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>What reading the record found.</summary>
    internal enum RecordState
    {
        /// <summary>The world save folder is not known yet; the store reads empty and must not be written.</summary>
        NotReady = 0,

        /// <summary>No record: build mode is off as far as Core knows.</summary>
        Absent = 1,

        /// <summary>A record was read.</summary>
        Present = 2,

        /// <summary>The key is there but its value is not a record this build can read.</summary>
        Unreadable = 3,
    }

    // ---- plans ---------------------------------------------------------------------------------

    /// <summary>What one <c>on</c> does.</summary>
    internal readonly record struct OnPlan(List<string> Write, List<string> Record, List<string> AlreadyOn, List<string> ClearRetired);

    /// <summary>
    /// <c>.debug build on</c>: write every setting that reads false now; the record becomes the old
    /// record (if build mode was already on, unknown names included) plus those names. A setting
    /// that already reads true and is not in the record was on before build mode, so it is neither
    /// written nor recorded. A <see cref="Retired"/> setting the old record names was turned on by
    /// an earlier build mode: it is cleared (<see cref="OnPlan.ClearRetired"/>, when it still reads
    /// on) and leaves the record. Unknown names stay in the record. Only call this with a record
    /// that was actually read (or is absent); an unreadable record must refuse <c>on</c>, never be
    /// replaced.
    /// </summary>
    internal static OnPlan PlanOn(Func<string, bool> current, IReadOnlyCollection<string>? record)
    {
        var write = new List<string>();
        var already = new List<string>();
        var keep = new HashSet<string>(record ?? Array.Empty<string>(), StringComparer.Ordinal);
        var next = new List<string>();
        foreach (var s in Settings)
        {
            bool on = current(s.Name);
            if (!on) write.Add(s.Name);
            else if (!keep.Contains(s.Name)) already.Add(s.Name);
            if (!on || keep.Contains(s.Name)) next.Add(s.Name);
        }
        if (record != null)
            foreach (var name in record)
                if (OffsetOf(name) < 0 && !next.Contains(name)) next.Add(name);
        return new OnPlan(write, next, already, RetiredToClear(current, record));
    }

    /// <summary>
    /// The <see cref="Retired"/> settings <paramref name="record"/> names that still read on: an
    /// earlier build mode turned them on, so this one turns them back.
    /// </summary>
    internal static List<string> RetiredToClear(Func<string, bool> current, IReadOnlyCollection<string>? record)
    {
        var clear = new List<string>();
        if (record == null) return clear;
        foreach (var s in Retired)
            foreach (var name in record)
                if (name == s.Name && current(s.Name) && !clear.Contains(s.Name)) clear.Add(s.Name);
        return clear;
    }

    /// <summary>A record's names without the <see cref="Retired"/> ones (what an adopt keeps).</summary>
    internal static List<string> WithoutRetired(IReadOnlyCollection<string> record)
    {
        var kept = new List<string>();
        foreach (var name in record)
            if (!IsRetired(name)) kept.Add(name);
        return kept;
    }

    /// <summary>What one <c>off</c> (or the after-load restore) does.</summary>
    internal readonly record struct OffPlan(List<string> Clear, List<string> Unknown);

    /// <summary>
    /// <c>.debug build off</c> and the after-load restore: clear each recorded setting that still
    /// reads true, by name. A recorded one that already reads false (someone turned it off) is left
    /// alone; unrecorded settings are never touched. Names this build does not switch are returned
    /// in <see cref="OffPlan.Unknown"/> for the log.
    /// </summary>
    internal static OffPlan PlanOff(Func<string, bool> current, IReadOnlyCollection<string> record)
    {
        var clear = new List<string>();
        var unknown = new List<string>();
        var named = new HashSet<string>(record, StringComparer.Ordinal);
        foreach (var s in Tracked())
            if (named.Contains(s.Name) && current(s.Name)) clear.Add(s.Name);
        foreach (var name in record)
            if (OffsetOf(name) < 0 && !unknown.Contains(name)) unknown.Add(name);
        return new OffPlan(clear, unknown);
    }

    /// <summary>
    /// <c>off</c> with no readable record: every switched setting that reads on. What was on before
    /// build mode cannot be told apart any more, so all of them go; the reply says so. A
    /// <see cref="Retired"/> setting is not among them: build mode no longer sets it, so with no
    /// record naming it, it is an operator's own.
    /// </summary>
    internal static List<string> PlanOffWithoutRecord(Func<string, bool> current)
    {
        var clear = new List<string>();
        foreach (var s in Settings)
            if (current(s.Name)) clear.Add(s.Name);
        return clear;
    }

    /// <summary>The offset of a switched or <see cref="Retired"/> setting by its game field name; -1 for any other name.</summary>
    internal static int OffsetOf(string name)
    {
        foreach (var s in Tracked())
            if (s.Name == name) return s.Offset;
        return -1;
    }

    // ---- lifetime ------------------------------------------------------------------------------

    /// <summary>What to do with a record found when no command is running.</summary>
    internal enum Recovery
    {
        /// <summary>No record: nothing to do.</summary>
        None = 0,

        /// <summary>A Core swap in the same server process: build mode is still on, keep the record.</summary>
        Adopt = 1,

        /// <summary>A world load or a new server process: build mode does not survive it. Restore and drop the record.</summary>
        RestoreAndClear = 2,
    }

    /// <summary>
    /// Build mode survives a Core swap and does not survive a world load or a server restart.
    /// <paramref name="afterWorldLoad"/>: this Core generation saw the world load. Otherwise (Core
    /// installed into a running world), the record's process stamp decides: written by this server
    /// process, it is a swap (adopt); written by another process or with no stamp, the world was
    /// loaded since (a restart whose restore an earlier generation never ran), so restore.
    /// </summary>
    internal static Recovery Decide(Record? record, bool afterWorldLoad, string processStamp)
    {
        if (record == null) return Recovery.None;
        if (afterWorldLoad) return Recovery.RestoreAndClear;
        return record.ProcessStamp.Length > 0 && record.ProcessStamp == Clean(processStamp)
            ? Recovery.Adopt
            : Recovery.RestoreAndClear;
    }

    // ---- replies -------------------------------------------------------------------------------

    internal const string ServerWideNote = "Server-wide: every player on this server builds this way while it is on.";

    internal const string NotReadyReply = "The world save folder is not known yet; try .debug build again in a moment.";

    internal const string NoSingletonReply = "The server's debug settings are not available in this world; nothing changed.";

    internal const string UnreadableOnReply =
        "Build mode's record in this save cannot be read, so on was refused (it would lose what an earlier on changed). "
        + "Run .debug build off first; with no readable record it turns every build setting off.";

    /// <summary>One line naming every switched setting and whether it reads on now.</summary>
    internal static string FormatSettings(Func<string, bool> current)
    {
        var sb = new StringBuilder();
        foreach (var s in Tracked())
        {
            if (sb.Length > 0) sb.Append(", ");
            sb.Append(s.Name).Append('=').Append(current(s.Name) ? "on" : "off");
        }
        return sb.ToString();
    }

    /// <summary>The status lines: mode, what it changed, any disagreement, and the live value of each setting.</summary>
    internal static List<string> StatusLines(RecordState state, IReadOnlyCollection<string>? record, Func<string, bool> current)
    {
        var lines = new List<string>();
        var on = PlanOffWithoutRecord(current);
        switch (state)
        {
            case RecordState.Present when record != null:
            {
                lines.Add("Build mode: on. " + ServerWideNote);
                var known = new List<string>();
                foreach (var n in record) if (OffsetOf(n) >= 0) known.Add(n);
                lines.Add(known.Count == 0
                    ? "It changed nothing (every setting was already on); off leaves them on."
                    : "Off turns back: " + string.Join(", ", known) + ".");
                var drifted = new List<string>();
                foreach (var n in known) if (!current(n)) drifted.Add(n);
                if (drifted.Count > 0)
                    lines.Add("Warning: recorded as turned on but now off: " + string.Join(", ", drifted) + ".");
                break;
            }
            case RecordState.Unreadable:
                lines.Add("Build mode: unknown. Warning: its record in this save cannot be read"
                    + (on.Count > 0 ? "; build settings are on: " + string.Join(", ", on) + "." : ".")
                    + " .debug build off turns every build setting off.");
                break;
            case RecordState.NotReady:
                lines.Add("Build mode: unknown (the world save folder is not known yet, so the record could not be read).");
                break;
            default:
                lines.Add(on.Count == 0
                    ? "Build mode: off."
                    : "Build mode: off, but warning: build settings are on with no record of build mode: "
                      + string.Join(", ", on) + ". .debug build off turns them all off.");
                break;
        }
        string retired = RetiredWarning(current, state == RecordState.Present ? record : null);
        if (retired.Length > 0) lines.Add(retired);
        lines.Add("Settings: " + FormatSettings(current) + ".");
        return lines;
    }

    /// <summary>
    /// A warning naming each <see cref="Retired"/> setting that reads on (pieces built now join no
    /// castle), or empty. Whether <c>off</c> turns it back depends on <paramref name="record"/>
    /// naming it; otherwise it is an operator's own and build mode leaves it.
    /// </summary>
    internal static string RetiredWarning(Func<string, bool> current, IReadOnlyCollection<string>? record)
    {
        var ours = new List<string>();
        var theirs = new List<string>();
        foreach (var s in Retired)
        {
            if (!current(s.Name)) continue;
            bool named = false;
            if (record != null)
                foreach (var n in record)
                    if (n == s.Name) named = true;
            (named ? ours : theirs).Add(s.Name);
        }
        var parts = new List<string>();
        if (ours.Count > 0)
            parts.Add(string.Join(", ", ours) + " is on (an earlier build mode set it), so pieces built now join no castle; .debug build off turns it off.");
        if (theirs.Count > 0)
            parts.Add(string.Join(", ", theirs) + " is on and build mode did not set it, so pieces built now join no castle; "
                + "build mode leaves it alone: turn it off in the server's debug settings.");
        return parts.Count == 0 ? "" : "Warning: " + string.Join(" ", parts) + " .debug build detached lists pieces built that way.";
    }

    /// <summary>The reply to <c>on</c> after the writes.</summary>
    internal static string FormatOn(OnPlan plan, bool wasOn)
    {
        string head = wasOn ? "Build mode was already on" : "Build mode on";
        string turned = plan.Write.Count == 0 ? "nothing needed turning on" : "turned on " + string.Join(", ", plan.Write);
        string kept = plan.AlreadyOn.Count == 0 ? "" : "; already on before (left as is by off): " + string.Join(", ", plan.AlreadyOn);
        string retired = plan.ClearRetired.Count == 0
            ? ""
            : "; turned off " + string.Join(", ", plan.ClearRetired) + " (an earlier build mode turned it on; pieces built with it join no castle)";
        return head + ": " + turned + kept + retired + ". " + ServerWideNote;
    }

    /// <summary>The reply to <c>off</c> with a record, after the writes.</summary>
    internal static string FormatOff(OffPlan plan) =>
        plan.Clear.Count == 0
            ? "Build mode off: nothing to turn back (the settings it changed were already off)."
            : "Build mode off: turned back " + string.Join(", ", plan.Clear) + ".";

    /// <summary>The reply to <c>off</c> with no readable record, after the writes.</summary>
    internal static string FormatOffWithoutRecord(List<string> cleared, bool unreadable)
    {
        string why = unreadable ? "Build mode's record in this save cannot be read" : "There is no record of build mode";
        return cleared.Count == 0
            ? why + "; every build setting is already off. Nothing changed."
            : why + ", so every build setting that was on was turned off (including any that were on before build mode): "
              + string.Join(", ", cleared) + ".";
    }
}
