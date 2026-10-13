using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Progenitor.Content;

/// <summary>
/// Game-free checks for cloned-item wire fields and the ItemKeyLookup overlay. The registrar
/// and the unit tests share this so the 1–63 ASCII rule and the pointer/length guards cannot
/// drift from what the live apply path enforces.
/// </summary>
internal static class PrefabWire
{
    /// <summary>Size of the ASCII id / localized-name / description slot on the wire, including the NUL.</summary>
    public const int AsciiCapacity = 64;

    /// <summary>Longest string that fits the ASCII slot with a terminator. Same bound as <c>id</c>.</summary>
    public const int AsciiMaxChars = AsciiCapacity - 1;

    /// <summary>
    /// Suffix Localization uses for a cloned item's description key (<c>id</c> + this). File-based
    /// <c>en.json</c> entries of that shape are applied even when <c>Described()</c> was omitted.
    /// </summary>
    public const string DescriptionKeySuffix = ".desc";

    /// <summary>
    /// Ints at the front of a definition packet: NewGuid, SourceGuid, MaxAmount, Kind
    /// (<see cref="PrefabKind"/> as int; 0 = item, 1 = buff).
    /// </summary>
    public const int PacketIntCount = 4;

    /// <summary>ASCII slots after the ints: id, localized name, localized description.</summary>
    public const int PacketAsciiFieldCount = 3;

    /// <summary>Byte offset of the first ASCII slot (after the leading ints).</summary>
    public const int PacketAsciiOffset = PacketIntCount * sizeof(int);

    /// <summary>
    /// Byte size of <c>PrefabDefinitionPacket</c>: four little-endian ints then three
    /// <see cref="AsciiCapacity"/>-byte ASCII fields. Tests round-trip this layout so the live
    /// struct cannot drift.
    /// </summary>
    public const int PacketSize = PacketAsciiOffset + (PacketAsciiFieldCount * AsciiCapacity);

    /// <summary>Localization key for a cloned item's description: <c>id.desc</c>.</summary>
    public static string DescriptionKey(string id)
    {
        if (string.IsNullOrEmpty(id))
            throw new ArgumentException("id is required.", nameof(id));
        return id + DescriptionKeySuffix;
    }

    /// <summary>
    /// Client-sync merge: keep <paramref name="existing"/> unless it is empty and
    /// <paramref name="incoming"/> is not. Used for LocalizedName and LocalizedDescription so a
    /// later packet fills fields the local registration left blank.
    /// </summary>
    public static string? FillEmpty(string? existing, string? incoming)
    {
        if (string.IsNullOrEmpty(existing) && !string.IsNullOrEmpty(incoming))
            return incoming;
        return existing;
    }

    /// <summary>
    /// Frames a pending apply is retried before it is given up. About 60 s at 60 Hz; long enough
    /// for <c>PrefabCollectionSystem</c> to finish after the gate opens, short enough that a
    /// missing source is not retried forever. On the client this budget also runs while the gate
    /// is still closed: a local-only registration never sees a server <c>PrfD</c>/<c>PrfK</c>
    /// (single-player, or a server without the mod) and must not retry forever. Client-only
    /// items require that packet; without it the definition is not applied.
    /// </summary>
    public const int ApplyGiveUpFrames = 3600;

    /// <summary>
    /// Required ASCII field: 1–63 bytes, no high-bit characters. Throws
    /// <see cref="ArgumentOutOfRangeException"/> for length and <see cref="ArgumentException"/>
    /// for non-ASCII so existing <c>DefineItem</c> / <c>Named</c> callers keep catching the same classes.
    /// </summary>
    public static void ValidateAscii(string value, string paramName, string field)
    {
        if (!TryValidateAscii(value, field, out var error, out bool lengthProblem))
        {
            if (lengthProblem)
                throw new ArgumentOutOfRangeException(paramName, error);
            throw new ArgumentException(error, paramName);
        }
    }

    /// <summary>
    /// Non-throwing form of <see cref="ValidateAscii"/>. <paramref name="lengthProblem"/> is set
    /// when the failure is empty/too-long rather than non-ASCII, so callers can pick an exception.
    /// </summary>
    public static bool TryValidateAscii(string? value, string field, out string error, out bool lengthProblem)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = $"{field} must be 1–{AsciiMaxChars} ASCII bytes.";
            lengthProblem = true;
            return false;
        }

        var bytes = Encoding.ASCII.GetBytes(value);
        if (bytes.Length == 0 || bytes.Length > AsciiMaxChars)
        {
            error = $"{field} must be 1–{AsciiMaxChars} ASCII bytes.";
            lengthProblem = true;
            return false;
        }

        for (int i = 0; i < value.Length; i++)
        {
            if (value[i] > 0x7F)
            {
                error = $"{field} '{value}' is not ASCII; the wire format is a {AsciiCapacity}-byte ASCII field.";
                lengthProblem = false;
                return false;
            }
        }

        error = "";
        lengthProblem = false;
        return true;
    }

    /// <summary>
    /// Optional ASCII field (localized name or description on the wire). Null or empty is valid
    /// and means "keep the source prefab's text". Non-empty values must pass
    /// <see cref="TryValidateAscii"/>.
    /// </summary>
    public static bool TryValidateOptionalAscii(string? value, string field, out string error)
    {
        if (string.IsNullOrEmpty(value))
        {
            error = "";
            return true;
        }
        return TryValidateAscii(value, field, out error, out _);
    }

    /// <summary>Positive integer that is a power of two (hash-map bucket counts).</summary>
    public static bool IsPowerOfTwo(int n) => n > 0 && (n & (n - 1)) == 0;

    /// <summary>Collections 2.x list invariant: a live length sits inside allocated capacity.</summary>
    public static bool IsValidNativeLength(int length, int capacity) =>
        length >= 0 && length <= capacity;

    /// <summary>
    /// Overlay is safe to walk only when every pointer is non-null, the list length sits
    /// inside capacity, buckets are a positive power of two (so <c>hash &amp; (n-1)</c> is a
    /// valid index), and values are 4-byte ints. A wrong overlay after a game patch, or a
    /// corrupt singleton, must fail the call rather than dereference.
    /// </summary>
    public static bool IsValidItemKeyOverlay(
        bool ptr,
        bool keys,
        bool values,
        bool next,
        bool buckets,
        int length,
        int capacity,
        int bucketCapacity,
        int sizeOfTValue,
        out string error)
    {
        if (!ptr)
        {
            error = "list Ptr is null";
            return false;
        }
        if (!keys)
        {
            error = "map Keys is null";
            return false;
        }
        if (!values)
        {
            error = "map Values is null";
            return false;
        }
        if (!next)
        {
            error = "map Next is null";
            return false;
        }
        if (!buckets)
        {
            error = "map Buckets is null";
            return false;
        }
        if (!IsValidNativeLength(length, capacity))
        {
            error = $"list length {length} outside 0..{capacity}";
            return false;
        }
        if (bucketCapacity <= 0 || !IsPowerOfTwo(bucketCapacity))
        {
            error = $"BucketCapacity {bucketCapacity} is not a positive power of two";
            return false;
        }
        if (sizeOfTValue != 4)
        {
            error = $"unexpected value size {sizeOfTValue}";
            return false;
        }

        error = "";
        return true;
    }

    /// <summary>
    /// Encode one definition packet into <paramref name="dest"/> (must be at least
    /// <see cref="PacketSize"/> bytes). Validates id as required ASCII and name/description as
    /// optional ASCII so a test of this helper is a test of what <c>TryFrom</c> will accept.
    /// </summary>
    public static bool TryWriteDefinitionPacket(
        byte[] dest,
        int newGuid,
        int sourceGuid,
        int maxAmount,
        string id,
        string? name,
        string? description,
        out string error) =>
        TryWriteDefinitionPacket(dest, newGuid, sourceGuid, maxAmount, (int)PrefabKind.Item, id, name, description, out error);

    /// <summary>
    /// Encode one definition packet, including <paramref name="kind"/>
    /// (<see cref="PrefabKind"/> as int). Buffs send 1 so the client does not require ItemData.
    /// </summary>
    public static bool TryWriteDefinitionPacket(
        byte[] dest,
        int newGuid,
        int sourceGuid,
        int maxAmount,
        int kind,
        string id,
        string? name,
        string? description,
        out string error)
    {
        if (dest == null)
        {
            error = "destination is null";
            return false;
        }
        if (dest.Length < PacketSize)
        {
            error = $"destination length {dest.Length} is below PacketSize {PacketSize}";
            return false;
        }
        if (!TryValidateAscii(id, "id", out error, out _))
            return false;
        if (!TryValidateOptionalAscii(name, "name", out error))
            return false;
        if (!TryValidateOptionalAscii(description, "description", out error))
            return false;
        if (!TryValidateKind(kind, out error))
            return false;

        WriteInt32(dest, 0, newGuid);
        WriteInt32(dest, 4, sourceGuid);
        WriteInt32(dest, 8, maxAmount);
        WriteInt32(dest, 12, kind);
        if (!TryWriteAsciiField(dest, PacketAsciiOffset, id, "id", out error))
            return false;
        if (!TryWriteAsciiField(dest, PacketAsciiOffset + AsciiCapacity, name, "name", out error))
            return false;
        if (!TryWriteAsciiField(dest, PacketAsciiOffset + (2 * AsciiCapacity), description, "description", out error))
            return false;
        error = "";
        return true;
    }

    /// <summary>
    /// Decode a buffer written by <see cref="TryWriteDefinitionPacket"/>. Does not re-validate
    /// ASCII (a well-formed write already did); a short buffer fails.
    /// </summary>
    public static bool TryReadDefinitionPacket(
        byte[] src,
        out int newGuid,
        out int sourceGuid,
        out int maxAmount,
        out string id,
        out string name,
        out string description,
        out string error) =>
        TryReadDefinitionPacket(src, out newGuid, out sourceGuid, out maxAmount, out _, out id, out name, out description, out error);

    /// <summary>
    /// Decode a buffer written by <see cref="TryWriteDefinitionPacket"/>. Does not re-validate
    /// ASCII (a well-formed write already did); a short buffer fails. <paramref name="kind"/> is
    /// the 4th int (<see cref="PrefabKind"/>).
    /// </summary>
    public static bool TryReadDefinitionPacket(
        byte[] src,
        out int newGuid,
        out int sourceGuid,
        out int maxAmount,
        out int kind,
        out string id,
        out string name,
        out string description,
        out string error)
    {
        newGuid = 0;
        sourceGuid = 0;
        maxAmount = 0;
        kind = 0;
        id = "";
        name = "";
        description = "";
        if (src == null)
        {
            error = "source is null";
            return false;
        }
        if (src.Length < PacketSize)
        {
            error = $"source length {src.Length} is below PacketSize {PacketSize}";
            return false;
        }

        newGuid = ReadInt32(src, 0);
        sourceGuid = ReadInt32(src, 4);
        maxAmount = ReadInt32(src, 8);
        kind = ReadInt32(src, 12);
        id = ReadAsciiField(src, PacketAsciiOffset);
        name = ReadAsciiField(src, PacketAsciiOffset + AsciiCapacity);
        description = ReadAsciiField(src, PacketAsciiOffset + (2 * AsciiCapacity));
        error = "";
        return true;
    }

    /// <summary>
    /// Wire <see cref="PrefabKind"/>: only Item (0) and Buff (1). Anything else is a refuse so a
    /// corrupt packet cannot take an unknown apply path.
    /// </summary>
    public static bool TryValidateKind(int kind, out string error)
    {
        if (kind == (int)PrefabKind.Item || kind == (int)PrefabKind.Buff)
        {
            error = "";
            return true;
        }
        error = $"kind {kind} is not a PrefabKind (0 = item, 1 = buff)";
        return false;
    }

    /// <summary>Map a validated wire int onto <see cref="PrefabKind"/>. Unknown values are Item.</summary>
    public static PrefabKind KindFromWire(int kind) =>
        kind == (int)PrefabKind.Buff ? PrefabKind.Buff : PrefabKind.Item;

    private static void WriteInt32(byte[] dest, int offset, int value)
    {
        dest[offset] = (byte)value;
        dest[offset + 1] = (byte)(value >> 8);
        dest[offset + 2] = (byte)(value >> 16);
        dest[offset + 3] = (byte)(value >> 24);
    }

    private static int ReadInt32(byte[] src, int offset) =>
        src[offset]
        | (src[offset + 1] << 8)
        | (src[offset + 2] << 16)
        | (src[offset + 3] << 24);

    private static bool TryWriteAsciiField(byte[] dest, int offset, string? value, string field, out string error)
    {
        error = "";
        var text = value ?? "";
        var bytes = Encoding.ASCII.GetBytes(text);
        if (bytes.Length >= AsciiCapacity)
        {
            error = $"{field} '{value}' exceeds {AsciiMaxChars} ASCII bytes";
            return false;
        }
        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] > 0x7F)
            {
                error = $"{field} '{value}' is not ASCII";
                return false;
            }
        }
        for (int i = 0; i < AsciiCapacity; i++)
            dest[offset + i] = i < bytes.Length ? bytes[i] : (byte)0;
        return true;
    }

    private static string ReadAsciiField(byte[] src, int offset)
    {
        int len = 0;
        while (len < AsciiCapacity && src[offset + len] != 0) len++;
        return Encoding.ASCII.GetString(src, offset, len);
    }
}

/// <summary>
/// Game-free bookkeeping for cloned-item apply. The registrar and the unit tests share this
/// so a vanilla spawnable name (or any GUID this process did not clone) cannot stay in
/// <c>Entries</c>, go out as <c>PrfD</c>, or be tombstoned as if we appended the key row.
/// </summary>
internal static class PrefabApplyBookkeeping
{
    /// <summary>
    /// Distinctive token in a refused-apply report when the GUID or spawnable name belongs
    /// to a prefab this process did not clone (vanilla name reuse / hash collision).
    /// </summary>
    public const string NotClonedToken = "not cloned by this process";

    /// <summary>
    /// Distinctive token in a refused-apply report when the spawnable name is already mapped
    /// (ours under a different GUID, or someone else's).
    /// </summary>
    public const string SpawnableMappedToken = "already maps to";

    /// <summary>
    /// Whether a spawnable-name dictionary entry may be claimed. The name is already mapped
    /// to <paramref name="mappedGuidHash"/>. Allowed only when this process cloned that GUID
    /// <em>and</em> it is this definition's own target (re-register after soft unapply).
    /// Equal hashes are not enough: <c>DefineItem("Item_Ingredient_Crystal")</c> hashes to
    /// the vanilla crystal GUID, which already occupies that name.
    /// </summary>
    public static bool CanClaimMappedSpawnableName(int mappedGuidHash, int targetGuidHash, bool mappedWasClonedHere) =>
        mappedWasClonedHere && mappedGuidHash == targetGuidHash;

    /// <summary>
    /// A GUID already in <c>_PrefabGuidToEntityMap</c> may be updated in place only when
    /// this process cloned it in this generation or an earlier one (snapshot-rehydrated,
    /// including a clone retired with the entity kept). Otherwise apply is a hard refuse
    /// (vanilla / another process or mod). Kind does not matter: item and buff share this
    /// gate. Evidence 97: a buff retired on Core swap was missing from the cloned set and
    /// was refused as foreign.
    /// </summary>
    public static bool MustRefuseExistingGuid(bool haveExisting, bool clonedHere) =>
        haveExisting && !clonedHere;

    /// <summary>
    /// Soft unapply that kept the clone entity records the GUID as cloned-here so a
    /// Core-swap snapshot still owns it. Items already get this via key-table tombstones
    /// (unioned into the cloned set); buffs never append, so they never tombstone — without
    /// this they vanish from the snapshot and the next generation refuses them. A never-applied
    /// (pending) registration must not claim a vanilla GUID whose hash collided with the id.
    /// </summary>
    public static bool ShouldRememberRetiredClone(bool entityKept, bool appliedHere) =>
        entityKept && appliedHere;

    /// <summary>
    /// Log fragment appended when <see cref="ShouldRememberRetiredClone"/> is true.
    /// </summary>
    public const string OwnershipKeptToken = "ownership kept for snapshot";

    /// <summary>Suffix on the unregister line when clone ownership is kept for the snapshot.</summary>
    public static string FormatRetiredOwnership(bool kept) =>
        kept ? "; " + OwnershipKeptToken : "";

    /// <summary>
    /// Snapshot cloned set: live clones, retired-but-kept clones (buff or item), and
    /// tombstones. A buff with no key row still belongs here; a GUID that is only in
    /// someone else's maps does not.
    /// </summary>
    public static int[] UnionClonedHashes(int[]? liveCloned, int[]? retiredOrApplied, int[]? tombstones)
    {
        var set = new HashSet<int>();
        AddHashes(set, liveCloned);
        AddHashes(set, retiredOrApplied);
        AddHashes(set, tombstones);
        if (set.Count == 0) return Array.Empty<int>();
        var copy = new int[set.Count];
        set.CopyTo(copy);
        return copy;
    }

    private static void AddHashes(HashSet<int> set, int[]? hashes)
    {
        if (hashes == null) return;
        for (int i = 0; i < hashes.Length; i++)
            set.Add(hashes[i]);
    }

    /// <summary>
    /// Keep the in-memory Entry after an apply attempt. Success and "source not ready yet"
    /// stay queued so a later WorldReady / first packet / frame retry can finish. Any other
    /// failure is dropped: not sent as <c>PrfD</c>, not tombstoned.
    /// </summary>
    public static bool KeepEntryAfterApply(bool appliedOk, bool stillPending) =>
        appliedOk || stillPending;

    /// <summary>
    /// Tombstone (and send <c>PrfK</c>) only for a GUID this process actually appended to
    /// ItemKeyLookup. A vanilla row that already contained the hash is never ours, even
    /// when <c>KeyTableHas</c> is true. Buff clones never append, so they never tombstone.
    /// </summary>
    public static bool ShouldTombstone(bool appendedHere) => appendedHere;

    /// <summary>
    /// Join-hash hole close: an appended GUID whose live registry entry is not
    /// <see cref="PrefabKind.Item"/> still occupies an ItemKeyLookup row, so it must
    /// stay a tombstone (<c>PrfK</c>) even though <c>Entries</c> already holds the new
    /// buff (or nothing). A live item of the same GUID is the re-register path and
    /// must not tombstone (the row is still the item's).
    /// </summary>
    public static bool ShouldTombstone(bool appendedHere, bool hasLiveEntry, PrefabKind liveKind)
    {
        if (!appendedHere) return false;
        if (!hasLiveEntry) return true;
        return liveKind != PrefabKind.Item;
    }

    /// <summary>
    /// A just-appended ItemKeyLookup row must pop when this apply did not land, otherwise
    /// the server table is longer than <c>PrfD</c>+<c>PrfK</c> and join-time count+hash
    /// fails. Already-present rows (tombstone / prior apply) are not new and stay.
    /// </summary>
    public static bool ShouldRollbackKey(bool appliedOk, bool keyWasNew) => !appliedOk && keyWasNew;

    /// <summary>
    /// Distinctive refuse when <see cref="PrefabKind.Item"/> is applied to a source with no
    /// <c>ItemData</c>. Fatal (not pending): the source is loaded, it is just the wrong kind.
    /// </summary>
    public const string ItemSourceRejectedToken = "source has no ItemData (DefineItem requires an item prefab)";

    /// <summary>
    /// Distinctive refuse when <see cref="PrefabKind.Buff"/> is applied to a source with no
    /// <c>Buff</c>. Fatal (not pending), same shape as <see cref="ItemSourceRejectedToken"/>.
    /// </summary>
    public const string BuffSourceRejectedToken = "source has no Buff (DefineBuff requires a buff prefab)";

    /// <summary>
    /// Item clones append <c>ItemKeyLookup</c> (inventory slots are an index into it). Buff
    /// clones skip that table so a custom buff cannot shift join-time count+hash.
    /// </summary>
    public static bool ShouldAppendItemKey(PrefabKind kind) => kind != PrefabKind.Buff;

    /// <summary>
    /// Source-kind gate used by the applicator. <see cref="PrefabKind.Item"/> needs
    /// <c>ItemData</c>; <see cref="PrefabKind.Buff"/> needs <c>Buff</c>. Presence of the
    /// other component does not satisfy the required one.
    /// </summary>
    public static bool SourceMatchesKind(PrefabKind kind, bool sourceHasItemData, bool sourceHasBuff) =>
        kind == PrefabKind.Buff ? sourceHasBuff : sourceHasItemData;

    /// <summary>Refuse line for a source that failed <see cref="SourceMatchesKind"/>.</summary>
    public static string SourceKindRejectedMessage(PrefabKind kind) =>
        kind == PrefabKind.Buff ? BuffSourceRejectedToken : ItemSourceRejectedToken;

    /// <summary>
    /// Classify an apply-failure report: pending (retry) vs fatal (drop the Entry).
    /// Ownership refusals are never pending, even if the wording overlaps a "missing" probe.
    /// </summary>
    public static bool IsApplyStillPending(string? report)
    {
        if (string.IsNullOrEmpty(report)) return false;
        if (report.IndexOf(NotClonedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (report.IndexOf(SpawnableMappedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (report.IndexOf("item key table full", StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (report.IndexOf(ItemSourceRejectedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (report.IndexOf(BuffSourceRejectedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            return false;
        if (IsFatalIdentityRefuse(report))
            return false;
        return report.IndexOf("not found", StringComparison.OrdinalIgnoreCase) >= 0
               || report.IndexOf("missing", StringComparison.OrdinalIgnoreCase) >= 0
               || report.IndexOf("world missing", StringComparison.OrdinalIgnoreCase) >= 0
               || report.IndexOf("item key table:", StringComparison.OrdinalIgnoreCase) >= 0
               || report.IndexOf("singleton count", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    /// <summary>
    /// Registry summary line. <paramref name="appended"/> is GUIDs this process actually
    /// wrote into ItemKeyLookup (the only ones that may become tombstones).
    /// </summary>
    public static string FormatRegistryDescribe(int registered, int tombstones, int appended)
    {
        var sb = new StringBuilder();
        sb.Append("registered=").Append(registered);
        sb.Append(" tombstones=").Append(tombstones);
        sb.Append(" appended=").Append(appended);
        return sb.ToString();
    }

    /// <summary>One tombstone's contribution to <c>PrefabRegistry.Describe()</c>.</summary>
    public static string FormatTombstoneDetail(int guidHash, string? id) =>
        "; tombstone guid=" + guidHash + " id='" + (id ?? "") + "'";

    /// <summary>
    /// Byte offset of <c>PrefabGUID</c> in every generated <c>Snapshot_*</c> blob and in the
    /// <c>NetSnapshot</c> buffer <c>GetPrefabGUID</c> reads. Not a GUID-keyed
    /// <c>NetworkSnapshotType</c> — that shared component is a <c>ushort</c> schema id.
    /// </summary>
    public const int NetSnapshotPrefabGuidOffset = 0;

    /// <summary>
    /// Byte offset of <c>NetworkSnapshotType.Value</c> in the same blob (ushort). Schema only;
    /// do not retarget it to the clone GUID.
    /// </summary>
    public const int NetSnapshotTypeOffset = 4;

    /// <summary>
    /// PrefabGUID (4) + snapshot type (2). Shorter buffers are not a spawn snapshot header.
    /// </summary>
    public const int NetSnapshotHeaderMinBytes = 6;

    /// <summary>Apply-report fragment when the clone has no <c>NetSnapshot</c> buffer (items, non-networked buffs).</summary>
    public const string NetNoneToken = "net=none";

    /// <summary>Apply-report fragment when the buffer exists but is shorter than the header.</summary>
    public const string NetShortToken = "net=short";

    /// <summary>Apply-report fragment when the rewrite threw.</summary>
    public const string NetErrorToken = "net=error";

    /// <summary>True when the byte buffer is long enough to hold PrefabGUID + snapshot type.</summary>
    public static bool NetSnapshotHeaderIsWritable(int byteLength) =>
        byteLength >= NetSnapshotHeaderMinBytes;

    /// <summary>
    /// Rewrite the blob GUID when Instantiate copied the source prefab's identity.
    /// Equal hashes mean the clone already owns the spawn header.
    /// </summary>
    public static bool ShouldRewriteNetSnapshotGuid(int storedGuidHash, int cloneGuidHash) =>
        storedGuidHash != cloneGuidHash;

    /// <summary>
    /// Write <paramref name="cloneGuidHash"/> at <see cref="NetSnapshotPrefabGuidOffset"/> of a
    /// NetSnapshot <em>byte</em> buffer. <paramref name="byteLength"/> is
    /// <c>GetBufferLength</c> on the byte buffer (not an element count of a generated
    /// <c>Snapshot_*</c> struct). Leaves the schema ushort at
    /// <see cref="NetSnapshotTypeOffset"/> alone. Returns false when the buffer is too
    /// short or the stored GUID already matches.
    /// </summary>
    public static bool TryRewriteNetSnapshotGuid(byte[]? buffer, int byteLength, int cloneGuidHash)
    {
        if (buffer == null || byteLength < 0 || byteLength > buffer.Length) return false;
        if (!NetSnapshotHeaderIsWritable(byteLength)) return false;
        int stored = ReadInt32LittleEndian(buffer, NetSnapshotPrefabGuidOffset);
        if (!ShouldRewriteNetSnapshotGuid(stored, cloneGuidHash)) return false;
        WriteInt32LittleEndian(buffer, NetSnapshotPrefabGuidOffset, cloneGuidHash);
        return true;
    }

    private static int ReadInt32LittleEndian(byte[] buffer, int offset) =>
        buffer[offset]
        | (buffer[offset + 1] << 8)
        | (buffer[offset + 2] << 16)
        | (buffer[offset + 3] << 24);

    private static void WriteInt32LittleEndian(byte[] buffer, int offset, int value)
    {
        buffer[offset] = (byte)value;
        buffer[offset + 1] = (byte)(value >> 8);
        buffer[offset + 2] = (byte)(value >> 16);
        buffer[offset + 3] = (byte)(value >> 24);
    }

    /// <summary>
    /// Apply-report fragment after a successful header read. Rewritten when
    /// <paramref name="storedGuidHash"/> is still the source. The live log is
    /// <c>PrefabRegistry: applied 'id' (guid) in World: … net=…</c>.
    /// </summary>
    public static string FormatNetIdentity(int cloneGuidHash, int snapshotType, int storedGuidHash)
    {
        var sb = new StringBuilder();
        sb.Append("net=").Append(cloneGuidHash);
        sb.Append(" type=").Append(snapshotType);
        if (ShouldRewriteNetSnapshotGuid(storedGuidHash, cloneGuidHash))
            sb.Append(" (was ").Append(storedGuidHash).Append(')');
        return sb.ToString();
    }

    /// <summary>Apply-report fragment when the buffer is too short to be a snapshot header.</summary>
    public static string FormatNetShort(int byteLength) =>
        NetShortToken + " (" + byteLength + ")";

    /// <summary>
    /// Distinctive token in a refused-apply report when same-id <c>CloneOf</c> changed
    /// and live instances still reference the clone.
    /// </summary>
    public const string SourceChangedToken = "source changed";

    /// <summary>
    /// Distinctive token in a refused-apply report when same-id <see cref="PrefabKind"/>
    /// flipped (Item↔Buff). A kind change is a different prefab.
    /// </summary>
    public const string KindChangedToken = "kind changed";

    /// <summary>Token in the source-changed refuse line (instance count + restart).</summary>
    public const string LiveInstancesToken = "live instance(s) exist";

    /// <summary>Apply-report fragment when the old clone was destroyed and InsertNew ran.</summary>
    public const string RetargetedToken = "retargeted";

    /// <summary>
    /// Exact log when a failed retarget could not snapshot or restore entity / asset /
    /// conversion / item-hash rows. One line for the whole rollback (R26 finding 13).
    /// </summary>
    public static string FormatRetargetRemapFailed(string id) =>
        "PrefabApplicator: retarget of '" + (id ?? "") + "' rolled back (bookkeeping rows)";

    /// <summary>Suffix on the kind-changed refuse: callers must pick a new id.</summary>
    public const string UseNewIdToken = "use a new id";

    /// <summary>Restart instruction on the source-changed refuse (instances block retarget).</summary>
    public const string RestartToRetargetToken = "restart to retarget";

    /// <summary>
    /// What a same-id apply does when the GUID is already a clone this process owns.
    /// Kind change is always refuse. Source change retargets only with zero live
    /// instances; otherwise refuse. In-place update still runs <c>SourceMatchesKind</c>
    /// on the existing entity.
    /// </summary>
    public enum SameIdApplyAction
    {
        /// <summary>Same kind, same source: patch the surviving clone.</summary>
        UpdateInPlace = 0,

        /// <summary>
        /// Same kind, different source, no live instances: destroy the old clone and
        /// <c>InsertNew</c> from the new source. GUID and key row stay.
        /// </summary>
        RetargetClone = 1,

        /// <summary>Same id, Item↔Buff. Fatal for this definition; use a new id.</summary>
        RefuseKindChange = 2,

        /// <summary>Source changed but live instances still reference the clone.</summary>
        RefuseSourceChange = 3,

        /// <summary>
        /// Same kind and source, but the existing entity failed
        /// <see cref="SourceMatchesKind"/>. Fatal for this definition, not the batch.
        /// </summary>
        RefuseExistingMismatch = 4,
    }

    /// <summary>
    /// Kind of an existing clone from its components. Buff clones carry <c>Buff</c>;
    /// item clones carry <c>ItemData</c>. A hybrid with both is treated as a buff
    /// (the HUD/replication path) so a later Item definition of that id is a kind change.
    /// </summary>
    public static PrefabKind InferKindFromComponents(bool hasItemData, bool hasBuff)
    {
        if (hasBuff) return PrefabKind.Buff;
        if (hasItemData) return PrefabKind.Item;
        return PrefabKind.Item;
    }

    /// <summary>
    /// Live-instance count the query could not take. Callers treat this as
    /// "instances exist" so a swallowed query cannot retarget a live buff.
    /// </summary>
    public const int LiveInstanceCountUnknown = -1;

    /// <summary>
    /// Fail-closed: a thrown or failed live-instance query counts as at least one
    /// instance so <see cref="DecideSameIdApply"/> refuses retarget.
    /// </summary>
    public static int LiveCountForDecision(int counted) =>
        counted < 0 ? 1 : counted;

    /// <summary>Exact log when the live-instance query threw and retarget is refused.</summary>
    public static string FormatLiveQueryFailedClosed(int guidHash) =>
        "PrefabApplicator: live-instance count for guid " + guidHash
        + " failed closed (treating as instances exist)";

    /// <summary>
    /// Exact server log after a same-id re-clone of a networked buff. Connected
    /// clients must rebuild their clone from <c>PrfD</c> before any instance replicates.
    /// </summary>
    public static string FormatPrfDAfterRetarget(string id) =>
        "PrefabRegistry: sent PrfD after retarget of '" + (id ?? "")
        + "' so connected clients rebuild the clone";

    /// <summary>
    /// Look up a remembered <c>CloneOf</c> hash from a Core-swap snapshot.
    /// Parallel arrays: <paramref name="cloneGuids"/>[i] was cloned from
    /// <paramref name="sourceHashes"/>[i]. Missing / length mismatch is unknown (0).
    /// </summary>
    public static bool TrySourceFromSnapshot(
        int cloneGuidHash,
        int[]? cloneGuids,
        int[]? sourceHashes,
        out int sourceHash)
    {
        sourceHash = 0;
        if (cloneGuids == null || sourceHashes == null) return false;
        int n = cloneGuids.Length;
        if (sourceHashes.Length != n) return false;
        for (int i = 0; i < n; i++)
        {
            if (cloneGuids[i] != cloneGuidHash) continue;
            sourceHash = sourceHashes[i];
            return sourceHash != 0;
        }
        return false;
    }

    /// <summary>
    /// Copy parallel clone-guid / source-hash arrays for the reclaim snapshot.
    /// Length mismatch drops the sources (unknown) rather than pairing the wrong hash.
    /// </summary>
    public static void CopyCloneSources(
        int[]? cloneGuids,
        int[]? sourceHashes,
        out int[] guids,
        out int[] sources)
    {
        if (cloneGuids == null || cloneGuids.Length == 0
            || sourceHashes == null || sourceHashes.Length != cloneGuids.Length)
        {
            guids = Array.Empty<int>();
            sources = Array.Empty<int>();
            return;
        }
        guids = new int[cloneGuids.Length];
        sources = new int[sourceHashes.Length];
        Array.Copy(cloneGuids, guids, cloneGuids.Length);
        Array.Copy(sourceHashes, sources, sourceHashes.Length);
    }

    /// <summary>
    /// Clear <c>UseParentNameAndDescription</c> only when both Header and description
    /// keys are written. Named-only writes Header and keeps the cloned parent
    /// description / flag so the source tooltip does not go blank.
    /// </summary>
    public static bool ShouldClearUseParent(bool wroteHeader, bool wroteDescription) =>
        wroteHeader && wroteDescription;

    /// <summary>
    /// Fallback when this generation did not remember the previous <c>CloneOf</c>
    /// (Core swap with no source-hash snapshot). Matching NetSnapshot presence and
    /// schema type means the clone still looks like the new source; a mismatch is a
    /// source change (Immaterial has no buffer, haste does). A remembered hash from
    /// the reclaim snapshot wins over this fingerprint.
    /// </summary>
    public static bool CloneLooksLikeSource(
        bool cloneHasNetSnapshot,
        int cloneSnapshotType,
        bool sourceHasNetSnapshot,
        int sourceSnapshotType)
    {
        if (cloneHasNetSnapshot != sourceHasNetSnapshot) return false;
        if (cloneHasNetSnapshot && cloneSnapshotType != sourceSnapshotType) return false;
        return true;
    }

    /// <summary>
    /// Whether resolved <c>CloneOf</c> differs from the clone's source. A remembered
    /// hash (this generation's last apply, the replaced definition, or the Core-swap
    /// reclaim snapshot) wins; hash 0 means unknown and the NetSnapshot fingerprint
    /// decides.
    /// </summary>
    public static bool SourceChanged(int rememberedSourceHash, int newSourceHash, bool fingerprintMatches)
    {
        if (rememberedSourceHash != 0) return rememberedSourceHash != newSourceHash;
        return !fingerprintMatches;
    }

    /// <summary>
    /// Same-id apply contract. Kind change is always refuse (do not retarget). Source
    /// change with live instances is refuse; with none is destroy-and-InsertNew.
    /// <paramref name="existingMatchesNewKind"/> is <see cref="SourceMatchesKind"/> on
    /// the existing entity and only gates the in-place path.
    /// </summary>
    public static SameIdApplyAction DecideSameIdApply(
        PrefabKind existingKind,
        PrefabKind newKind,
        bool existingMatchesNewKind,
        bool sourceChanged,
        int liveInstanceCount)
    {
        if (existingKind != newKind) return SameIdApplyAction.RefuseKindChange;
        if (sourceChanged)
            return liveInstanceCount > 0 ? SameIdApplyAction.RefuseSourceChange : SameIdApplyAction.RetargetClone;
        if (!existingMatchesNewKind) return SameIdApplyAction.RefuseExistingMismatch;
        return SameIdApplyAction.UpdateInPlace;
    }

    /// <summary>
    /// Exact refuse line when same-id <c>CloneOf</c> changed and live instances exist.
    /// Logged as Error; the definition is dropped (not pending).
    /// </summary>
    public static string FormatSourceChangedRefuse(string id, int wasSourceHash, int nowSourceHash, int liveCount) =>
        "PrefabRegistry: '" + (id ?? "") + "' " + SourceChangedToken
        + " (was " + FormatSourceHash(wasSourceHash) + ", now " + FormatSourceHash(nowSourceHash)
        + ") but " + liveCount + " " + LiveInstancesToken + "; " + RestartToRetargetToken;

    /// <summary>
    /// Exact refuse line when same-id Item↔Buff. Kind change is a different prefab.
    /// </summary>
    public static string FormatKindChangedRefuse(string id, PrefabKind was, PrefabKind now) =>
        "PrefabRegistry: '" + (id ?? "") + "' " + KindChangedToken
        + " (was " + was + ", now " + now + "); " + UseNewIdToken;

    /// <summary>Apply-report fragment after destroy-and-InsertNew of a same-id source change.</summary>
    public static string FormatRetargeted(int wasSourceHash, int nowSourceHash) =>
        RetargetedToken + " (was " + FormatSourceHash(wasSourceHash) + ", now " + FormatSourceHash(nowSourceHash) + ")";

    /// <summary>Remembered source hash 0 means this generation never saw the previous CloneOf.</summary>
    public static string FormatSourceHash(int sourceHash) =>
        sourceHash == 0 ? "unknown" : sourceHash.ToString();

    /// <summary>
    /// Fatal identity refuses (kind / source-with-instances) are never retried. The
    /// full <c>PrefabRegistry:</c> line is the report so the log matches the contract.
    /// </summary>
    public static bool IsFatalIdentityRefuse(string? report)
    {
        if (string.IsNullOrEmpty(report)) return false;
        if (report.IndexOf(KindChangedToken, StringComparison.OrdinalIgnoreCase) >= 0)
            return true;
        return report.IndexOf(SourceChangedToken, StringComparison.OrdinalIgnoreCase) >= 0
               && report.IndexOf(LiveInstancesToken, StringComparison.OrdinalIgnoreCase) >= 0;
    }
}

/// <summary>
/// Per-join budget for prefab rows a <em>server</em> pushes to this client (<c>PrfD</c>
/// definitions and <c>PrfK</c> key placeholders). Threat model surface 6: without it a hostile
/// server can send an unbounded number of them, each cloning an attacker-named prefab into the
/// client's world and appending a row to <c>ItemKeyLookup</c>, until the join-time count+hash
/// validation against a legitimate server fails.
///
/// One shared budget counts distinct GUIDs across both packet kinds; updates reuse a row. It is per
/// join — <see cref="BeginJoin"/> runs when the client apply gate opens for a new session
/// (<c>PrefabApplyGate.NotifyClientPacket</c> reporting <c>OpenedGate</c>), the same moment the
/// registry drops the previous join's <c>SyncedOwner</c> entries.
///
/// Reclaim: the client's key table is <b>not</b> part of a save. It is rebuilt every session by
/// <c>BuildItemKeyLookupSystem.OnUpdate</c> from live <c>RegisterPrefab</c> entities (add-if-not-
/// contains, IntroSort, rebuild <c>ItemToIndex</c>; internal research notes),
/// and <c>ItemKeyLookup</c> itself holds two <c>NativeContainer</c>s on a system entity, not
/// persisted state. Rows a server added therefore live only as long as the clone entities this
/// session created. A middle row must never be removed by hand (§5: inventory slots travel as
/// indices into this table, so compacting one desyncs every later slot), which is why the bound is
/// this cap and not a sweep.
/// </summary>
internal static class PrefabSyncBudget
{
    /// <summary>Default rows one server may add per join. Config <c>Networking.MaxSyncedPrefabsPerJoin</c>.</summary>
    public const int DefaultMaxPerJoin = 256;

    /// <summary>Experimental configured identity ceiling; native key-table headroom is checked separately.</summary>
    public const int ExperimentalMaxPerJoin = 16384;

    /// <summary>
    /// <c>PrfE</c> packets one server may spend per join (job FEDW3, R51 finding 2). The synced-row
    /// budget above is charged <em>once per prefab</em> on purpose — it bounds how many prefabs a
    /// server may reach into, not how often it corrects them — which left the packet itself with no
    /// bound at all: after the first packet for a guid, every further one for that guid was free,
    /// and each cost a parse, up to 16 type resolutions, up to 16 entity writes and up to 16 log
    /// lines. This is the bound on the channel; 512 corrections in one join is far past any honest
    /// server and far below a flood.
    /// </summary>
    public const int DefaultMaxEditPacketsPerJoin = 512;

    /// <summary>
    /// Total <c>PrfE</c> payload bytes one server may spend per join. 512 packets of the 15 KiB
    /// transport limit would be 7.5 MiB; a megabyte is the real ceiling and is reached first by a
    /// server sending large layers rather than many.
    /// </summary>
    public const int DefaultMaxEditBytesPerJoin = 1024 * 1024;

    private static readonly object Gate = new();
    private static int _cap = DefaultMaxPerJoin;
    private static readonly HashSet<int> Identities = new();
    private static readonly ItemStatSyncBudget RowPackets = new();
    private static int _definitions;
    private static int _placeholders;
    private static bool _capLogged;
    private static int _editPacketCap = DefaultMaxEditPacketsPerJoin;
    private static int _editByteCap = DefaultMaxEditBytesPerJoin;
    private static int _editPackets;
    private static long _editBytes;
    private static bool _editCapLogged;

    /// <summary>Rows accepted per join. Values clamp to 1-16384; the default remains 256.</summary>
    public static int Cap
    {
        get { lock (Gate) return _cap; }
        set { lock (Gate) _cap = Math.Clamp(value, 1, ExperimentalMaxPerJoin); }
    }

    /// <summary>Distinct identities first accepted as definitions this join.</summary>
    public static int Definitions
    {
        get { lock (Gate) return _definitions; }
    }

    /// <summary>Distinct identities first accepted as key placeholders this join.</summary>
    public static int Placeholders
    {
        get { lock (Gate) return _placeholders; }
    }

    /// <summary>Rows accepted this join, both kinds.</summary>
    public static int Accepted
    {
        get { lock (Gate) return _definitions + _placeholders; }
    }

    /// <summary>
    /// Start a new join's budget. <paramref name="previousJoinSummary"/> is the one-line report of
    /// what the last server added (null when it added nothing) so a player can see an abusive
    /// server in their own log.
    /// </summary>
    public static void BeginJoin(out string? previousJoinSummary)
    {
        lock (Gate)
        {
            previousJoinSummary = _definitions + _placeholders > 0
                ? Summary(_definitions, _placeholders)
                : null;
            Identities.Clear();
            RowPackets.Reset();
            _definitions = 0;
            _placeholders = 0;
            _capLogged = false;
            _editPackets = 0;
            _editBytes = 0;
            _editCapLogged = false;
        }
    }

    /// <summary>Reset without reporting. Uninstall and tests.</summary>
    public static void Reset()
    {
        lock (Gate)
        {
            Identities.Clear();
            RowPackets.Reset();
            _definitions = 0;
            _placeholders = 0;
            _capLogged = false;
            _editPackets = 0;
            _editBytes = 0;
            _editCapLogged = false;
        }
    }

    /// <summary><c>PrfE</c> packets accepted this join.</summary>
    public static int EditPackets
    {
        get { lock (Gate) return _editPackets; }
    }

    /// <summary><c>PrfE</c> payload bytes accepted this join.</summary>
    public static long EditBytes
    {
        get { lock (Gate) return _editBytes; }
    }

    /// <summary><c>PrfE</c> packets accepted per join. Values below 1 clamp to 1.</summary>
    public static int EditPacketCap
    {
        get { lock (Gate) return _editPacketCap; }
        set { lock (Gate) _editPacketCap = value < 1 ? 1 : value; }
    }

    /// <summary><c>PrfE</c> payload bytes accepted per join. Values below 1 clamp to 1.</summary>
    public static int EditByteCap
    {
        get { lock (Gate) return _editByteCap; }
        set { lock (Gate) _editByteCap = value < 1 ? 1 : value; }
    }

    /// <summary>
    /// Charge one received <c>PrfE</c> against the per-join packet and byte caps. False means the
    /// caller drops the packet before parsing anything out of it. Repeated edits still spend
    /// these packet and byte budgets; <paramref name="log"/> reports the first refusal per join.
    /// </summary>
    public static bool TryAdmitEditPacket(int payloadBytes, out string? log)
    {
        log = null;
        int bytes = payloadBytes < 0 ? 0 : payloadBytes;
        lock (Gate)
        {
            if (_editPackets >= _editPacketCap || _editBytes + bytes > _editByteCap)
            {
                if (!_editCapLogged)
                {
                    _editCapLogged = true;
                    log = EditCapLog(_editPacketCap, _editByteCap, _editPackets, _editBytes);
                }
                return false;
            }
            _editPackets++;
            _editBytes += bytes;
            return true;
        }
    }

    /// <summary>The one line a server that floods <c>PrfE</c> produces per join.</summary>
    public static string EditCapLog(int packetCap, int byteCap, int packets, long bytes) =>
        "PrefabRegistry: server exceeded the per-join PrfE budget ("
        + packetCap.ToString(CultureInfo.InvariantCulture) + " packet(s), "
        + byteCap.ToString(CultureInfo.InvariantCulture) + " byte(s); "
        + packets.ToString(CultureInfo.InvariantCulture) + " packet(s) and "
        + bytes.ToString(CultureInfo.InvariantCulture)
        + " byte(s) accepted); dropping every further PrfE until the next join.";

    /// <summary>
    /// Charge one <c>PrfD</c> against the budget. False means the caller drops the packet;
    /// Existing identities reuse their charge. The packet rate is bounded separately;
    /// <paramref name="log"/> reports the first row-cap refusal or first refusal in a rate window.
    /// </summary>
    public static bool TryAdmitDefinition(int guid, out string? log) => TryAdmit(guid, definition: true, out log);

    /// <summary>
    /// Charge one <c>PrfK</c> against the budget. False means the caller drops the packet;
    /// Existing identities reuse their charge. The packet rate is bounded separately;
    /// <paramref name="log"/> reports the first row-cap refusal or first refusal in a rate window.
    /// </summary>
    public static bool TryAdmitPlaceholder(int guid, out string? log) => TryAdmit(guid, definition: false, out log);

    /// <summary>The one line a server that blows the budget produces per join.</summary>
    public static string CapLog(int cap, int definitions, int placeholders) =>
        "PrefabRegistry: server exceeded the per-join cap of "
        + cap.ToString(CultureInfo.InvariantCulture)
        + " synced prefab row(s) (" + definitions.ToString(CultureInfo.InvariantCulture)
        + " definition(s), " + placeholders.ToString(CultureInfo.InvariantCulture)
        + " key placeholder(s) accepted); dropping new prefab identities until the next join. "
        + "Raise Networking.MaxSyncedPrefabsPerJoin only for a server you trust.";

    /// <summary>Per-join report of what a server added.</summary>
    public static string Summary(int definitions, int placeholders) =>
        "PrefabRegistry: server added "
        + definitions.ToString(CultureInfo.InvariantCulture)
        + " definition(s) and "
        + placeholders.ToString(CultureInfo.InvariantCulture)
        + " key placeholder(s) in the previous join.";

    private static bool TryAdmit(int guid, bool definition, out string? log)
    {
        log = null;
        lock (Gate)
        {
            if (!RowPackets.TryAdmit(out bool reportRate))
            {
                if (reportRate) log = "PrefabRegistry: prefab packet rate exceeded; dropping packets until the receive window renews.";
                return false;
            }
            // An unregister never refunds a row: the key table retains its stable index.
            // A tombstone or redefinition of that same identity does not append another row.
            if (Identities.Contains(guid)) return true;
            if (Identities.Count >= _cap)
            {
                if (!_capLogged)
                {
                    _capLogged = true;
                    log = CapLog(_cap, _definitions, _placeholders);
                }
                return false;
            }
            Identities.Add(guid);
            if (definition) _definitions++;
            else _placeholders++;
            return true;
        }
    }
}
