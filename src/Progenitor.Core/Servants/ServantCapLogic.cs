using System;
using System.Collections.Generic;
using System.Globalization;
using Progenitor.Castles;

namespace Progenitor.Servants;

/// <summary>
/// The success cap's arithmetic, game-free. At the return the game succeeds a servant with
/// probability <c>c</c> (its vanilla chance). Progenitor vetoes the roll (forces the game's own
/// failure) with probability <c>q = 1 - cap / c</c> when <c>c &gt; cap</c>, so the servant succeeds
/// with probability <c>(1 - q) · c = cap</c>. A cap at or above <c>c</c> never vetoes.
/// </summary>
internal static class ServantCapMath
{
    /// <summary>
    /// The gear level written for one update to force a failure: proficiency becomes about
    /// -99,900, so the shortfall squared over 15000 is far above 1 and the game's chance clamps to 0.
    /// The game's roll <c>NextFloat() &lt;= 0</c> can still pass, with probability about 1.2e-7.
    /// </summary>
    internal const float ForcedGearLevel = -10000f;

    /// <summary>The veto probability <c>1 - cap / chance</c>; 0 when the cap is at or above the chance, or the chance is 0.</summary>
    internal static double VetoProbability(float chance, float cap)
    {
        if (float.IsNaN(chance) || float.IsNaN(cap)) return 0;
        if (chance <= 0f || cap >= chance) return 0;
        if (cap <= 0f) return 1;
        return 1.0 - (double)cap / chance;
    }

    /// <summary>True when the draw <paramref name="uniform"/> (0 inclusive to 1 exclusive) vetoes this roll.</summary>
    internal static bool Veto(float chance, float cap, double uniform) => uniform < VetoProbability(chance, cap);

    /// <summary>The probability the servant succeeds overall: no veto times the game's chance.</summary>
    internal static double Overall(float chance, float cap) => (1.0 - VetoProbability(chance, cap)) * chance;

    /// <summary>A cap a request may carry: null, or 0..1.</summary>
    internal static bool IsValidCap(float? cap) => cap == null || (cap.Value >= 0f && cap.Value <= 1f);
}

/// <summary>One capped hunt in the cap book (Progenitor's server store), as JSON.</summary>
internal sealed class CapRecord
{
    /// <summary>The hunt's mission prefab GUID hash.</summary>
    public int MissionGuid { get; set; }

    /// <summary>The hunt's start ticks (<c>DateTime.UtcNow.Ticks</c> at the send).</summary>
    public long StartTicks { get; set; }

    /// <summary>The hunt's length in seconds as sent.</summary>
    public float LengthSeconds { get; set; }

    /// <summary>The castle's territory index at the send (-1 when unknown): the cross-check.</summary>
    public int Territory { get; set; } = -1;

    /// <summary>The cap, 0..1.</summary>
    public float MaxSuccess { get; set; }

    /// <summary>When the cap was recorded (UTC ticks).</summary>
    public long RecordedTicks { get; set; }
}

/// <summary>
/// The caps of the hunts Progenitor sent with <see cref="ServantSendRequest.MaxSuccess"/>, keyed by
/// mission GUID and start ticks (unique in practice: start ticks are the send's clock in 100 ns),
/// with the territory as a cross-check. Game-free; the runtime mirrors it into the server store
/// under <see cref="StoreKey"/> keys.
/// </summary>
internal sealed class ServantCapBook
{
    /// <summary>Prefix of the store keys that hold caps.</summary>
    internal const string StorePrefix = "cap:";

    /// <summary>How long after its end a cap whose hunt is gone from every heart is kept before it expires.</summary>
    internal const long ExpireGraceTicks = 10L * 60 * ServantLogic.TicksPerSecond;

    private readonly Dictionary<string, CapRecord> _caps = new(StringComparer.Ordinal);

    /// <summary>How many caps the book holds.</summary>
    internal int Count => _caps.Count;

    /// <summary>The book's key for a hunt: <c>mission:startTicks</c>.</summary>
    internal static string Key(int missionGuid, long startTicks) =>
        missionGuid.ToString(CultureInfo.InvariantCulture) + ":" + startTicks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The server store key for a hunt.</summary>
    internal static string StoreKey(int missionGuid, long startTicks) => StorePrefix + Key(missionGuid, startTicks);

    /// <summary>The book key inside a store key, or null when it is not a cap key.</summary>
    internal static string? FromStoreKey(string storeKey) =>
        storeKey != null && storeKey.StartsWith(StorePrefix, StringComparison.Ordinal) ? storeKey.Substring(StorePrefix.Length) : null;

    /// <summary>Add or replace a cap. Returns its store key.</summary>
    internal string Add(CapRecord record)
    {
        if (record == null) throw new ArgumentNullException(nameof(record));
        _caps[Key(record.MissionGuid, record.StartTicks)] = record;
        return StoreKey(record.MissionGuid, record.StartTicks);
    }

    /// <summary>
    /// The cap of a hunt. A record whose territory disagrees with the hunt's castle (both known) is
    /// not returned and <paramref name="territoryMismatch"/> is set: the key is someone else's.
    /// </summary>
    internal bool TryGet(int missionGuid, long startTicks, int territory, out CapRecord? record, out bool territoryMismatch)
    {
        territoryMismatch = false;
        if (!_caps.TryGetValue(Key(missionGuid, startTicks), out record)) return false;
        if (record.Territory >= 0 && territory >= 0 && record.Territory != territory)
        {
            territoryMismatch = true;
            record = null;
            return false;
        }
        return true;
    }

    /// <summary>Drop a hunt's cap; true when it was there.</summary>
    internal bool Remove(int missionGuid, long startTicks) => _caps.Remove(Key(missionGuid, startTicks));

    /// <summary>Drop every cap.</summary>
    internal void Clear() => _caps.Clear();

    /// <summary>Every cap, for the store sync and diagnostics.</summary>
    internal IReadOnlyList<CapRecord> All() => new List<CapRecord>(_caps.Values);

    /// <summary>
    /// The caps whose hunt ended more than <see cref="ExpireGraceTicks"/> ago and that no heart
    /// still holds (<paramref name="stillOut"/> answers by mission and start ticks): an orphan after a
    /// rollback to a save without the row, or a row that resolved while Progenitor was not watching.
    /// </summary>
    internal List<CapRecord> Expired(long nowTicks, Func<int, long, bool> stillOut)
    {
        var list = new List<CapRecord>();
        foreach (var r in _caps.Values)
        {
            long end = ServantLogic.CompletionTicks(r.StartTicks, r.LengthSeconds);
            if (end + ExpireGraceTicks < nowTicks && !stillOut(r.MissionGuid, r.StartTicks)) list.Add(r);
        }
        return list;
    }
}

/// <summary>What a temporary write changed.</summary>
internal enum TransientKind
{
    /// <summary><c>ServantPower.GearLevel</c> forced to <see cref="ServantCapMath.ForcedGearLevel"/> (a veto).</summary>
    GearLevel = 0,

    /// <summary><c>ActiveServantMission.MissionLengthSeconds</c> raised so the game does not finish the row this update (a hold).</summary>
    MissionLength = 1,
}

/// <summary>
/// One write the cap makes for one update and must undo: what, where, the value before and the
/// value written. The target is located by entity and validated by the world-load epoch plus, for
/// a servant, its network id (or, for a row, the row's key); a raw entity id alone is never trusted.
/// </summary>
internal sealed class TransientWrite
{
    public TransientKind Kind;
    public CastleId Target;
    public int Epoch;
    public byte[] NetworkId = Array.Empty<byte>();
    public (int, long, CastleId) RowKey;
    public float Original;
    public float Written;
    public long WrittenTicks;
    public int Territory = -1;
    public string Name = "";

    /// <summary>The server store key of its pending-restore record, "" when none was written.</summary>
    public string StoreKey = "";

    /// <summary>Restores of this write that threw; it is retried by the next guard until <see cref="TransientJournal.MaxAttempts"/>.</summary>
    public int Attempts;

    /// <summary><c>gear level of servant i:v (name) -10000 -> 12.5</c>, for log lines.</summary>
    public override string ToString() =>
        (Kind == TransientKind.GearLevel ? "gear level of servant " : "length of a hunt row of heart ") + Target
        + (Name.Length > 0 ? " (" + Name + ")" : "")
        + " " + Written.ToString("0.###", CultureInfo.InvariantCulture) + " -> " + Original.ToString("0.###", CultureInfo.InvariantCulture);
}

/// <summary>What a restore found.</summary>
internal enum RestoreAction
{
    /// <summary>The written value is still there: write the original back.</summary>
    Write = 0,

    /// <summary>The original is already back (nothing to do).</summary>
    AlreadyOriginal,

    /// <summary>Something else changed the value since: leave it and warn.</summary>
    ChangedElsewhere,

    /// <summary>The target is gone (a vetoed servant the game destroyed, a row that finished): nothing to restore.</summary>
    TargetGone,

    /// <summary>An entity is at the location but its epoch or network id is not the target's: never written.</summary>
    IdentityMismatch,
}

/// <summary>
/// The temporary writes of the cap and their restore, game-free bookkeeping. Every write is
/// journaled before it is made; every restore path (after the update, the update group's end, the
/// next persistence group, the next frame, Core unload) drains the journal, and a drain later than
/// the update's own after-hook is counted as late.
/// </summary>
internal sealed class TransientJournal
{
    private readonly List<TransientWrite> _pending = new();

    /// <summary>The writes not restored yet.</summary>
    internal IReadOnlyList<TransientWrite> Pending => _pending;

    /// <summary>How many writes are outstanding.</summary>
    internal int Count => _pending.Count;

    /// <summary>Restores made later than the update's own after-hook, since install.</summary>
    internal long LateRestores { get; private set; }

    /// <summary>Restores made, since install.</summary>
    internal long Restores { get; private set; }

    /// <summary>Journal a write (call before making it).</summary>
    internal void Add(TransientWrite write) => _pending.Add(write ?? throw new ArgumentNullException(nameof(write)));

    /// <summary>
    /// Take every outstanding write for a restore, newest first (so two writes to one place undo in
    /// order). <paramref name="late"/> counts them as late.
    /// </summary>
    internal List<TransientWrite> Drain(bool late)
    {
        var list = new List<TransientWrite>(_pending);
        list.Reverse();
        _pending.Clear();
        Restores += list.Count;
        if (late) LateRestores += list.Count;
        return list;
    }

    /// <summary>Forget everything (Core unload after the drain).</summary>
    internal void Clear() => _pending.Clear();

    /// <summary>How many times a restore that throws is tried before it is given up (and its record kept for the next Core).</summary>
    internal const int MaxAttempts = 3;

    /// <summary>
    /// A restore of <paramref name="write"/> threw: put it back for the next guard, or give up after
    /// <see cref="MaxAttempts"/>. True when it was put back.
    /// </summary>
    internal bool Retry(TransientWrite write)
    {
        write.Attempts++;
        if (write.Attempts >= MaxAttempts) return false;
        _pending.Add(write);
        return true;
    }

    /// <summary>What a restore of <paramref name="write"/> should do, from what is at its target now.</summary>
    internal static RestoreAction Decide(TransientWrite write, bool targetFound, bool identityOk, float current)
    {
        if (!targetFound) return RestoreAction.TargetGone;
        if (!identityOk) return RestoreAction.IdentityMismatch;
        if (current.Equals(write.Original)) return RestoreAction.AlreadyOriginal;
        if (current.Equals(write.Written)) return RestoreAction.Write;
        return RestoreAction.ChangedElsewhere;
    }
}

/// <summary>
/// A pending gear-level restore as written to the server store before the write, so a process that
/// stops between the write and its restore (a crash inside the update) leaves a record the next
/// Core generation or process can act on. Identity: the process, the world-load epoch, the entity
/// and its network id; across processes only the castle territory and the coffin's servant name,
/// and only for a servant whose gear level still reads exactly the forced value.
/// </summary>
internal sealed class GearRestoreRecord
{
    public string Process { get; set; } = "";
    public int Epoch { get; set; }
    public int Index { get; set; }
    public int Version { get; set; }
    public string NetworkId { get; set; } = "";
    public int Territory { get; set; } = -1;
    public string Name { get; set; } = "";
    public float Original { get; set; }
    public float Written { get; set; }
    public long WrittenTicks { get; set; }

    /// <summary>Prefix of the store keys that hold pending restores.</summary>
    internal const string StorePrefix = "restore:";

    /// <summary>The store key for a write: <c>restore:index:version:ticks</c>.</summary>
    internal static string StoreKey(CastleId servant, long ticks) =>
        StorePrefix + servant.Index.ToString(CultureInfo.InvariantCulture) + ":" + servant.Version.ToString(CultureInfo.InvariantCulture)
        + ":" + ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>Hex of a network id, for the record.</summary>
    internal static string Hex(byte[] bytes)
    {
        var sb = new System.Text.StringBuilder(bytes.Length * 2);
        foreach (var b in bytes) sb.Append(b.ToString("x2", CultureInfo.InvariantCulture));
        return sb.ToString();
    }

    /// <summary>
    /// For a record left by another process or world load: the servant it belongs to among those
    /// whose gear level reads exactly <see cref="Written"/>, matched by territory and coffin name;
    /// -1 when none or more than one match (never a guess).
    /// </summary>
    internal int MatchForeign(IReadOnlyList<(int Territory, string Name, float Gear)> forced)
    {
        int found = -1;
        for (int i = 0; i < forced.Count; i++)
        {
            var f = forced[i];
            if (!f.Gear.Equals(Written) || f.Territory != Territory || !string.Equals(f.Name, Name, StringComparison.Ordinal)) continue;
            if (found >= 0) return -1;
            found = i;
        }
        return found;
    }
}

/// <summary>What the hold check decided for one due row while the cap book is not readable.</summary>
internal enum HoldDecision
{
    /// <summary>Hold the row this update (raise its length for the update only).</summary>
    Hold = 0,

    /// <summary>The hold timed out: let the game finish the row with no cap (logged once).</summary>
    Release,
}

/// <summary>
/// Bounded holds of due rows while the cap book cannot be read (its store is not loaded yet): each
/// row is held update by update for at most <see cref="TimeoutTicks"/> from its first hold, then
/// released to the game uncapped. Game-free.
/// </summary>
internal sealed class HoldTracker
{
    /// <summary>The longest a row is held.</summary>
    internal const long TimeoutTicks = 30L * ServantLogic.TicksPerSecond;

    private readonly Dictionary<(CastleId, (int, long, CastleId)), long> _first = new();
    private readonly HashSet<(CastleId, (int, long, CastleId))> _released = new();

    /// <summary>Rows held at least once since install.</summary>
    internal long Held { get; private set; }

    /// <summary>Rows released uncapped after the timeout since install.</summary>
    internal long Released { get; private set; }

    /// <summary>Hold or release the row; <paramref name="firstRelease"/> is true the one time a row times out.</summary>
    internal HoldDecision Decide(CastleId heart, (int, long, CastleId) key, long nowTicks, out bool firstRelease)
    {
        firstRelease = false;
        var k = (heart, key);
        if (_released.Contains(k)) return HoldDecision.Release;
        if (!_first.TryGetValue(k, out long first))
        {
            _first[k] = nowTicks;
            Held++;
            return HoldDecision.Hold;
        }
        if (nowTicks - first < TimeoutTicks) return HoldDecision.Hold;
        _first.Remove(k);
        _released.Add(k);
        Released++;
        firstRelease = true;
        return HoldDecision.Release;
    }

    /// <summary>The book became readable: forget every hold.</summary>
    internal void Clear()
    {
        _first.Clear();
        _released.Clear();
    }

    /// <summary>Rows currently being held.</summary>
    internal int Active => _first.Count;
}
