using System;

namespace Progenitor.Ecs;

/// <summary>
/// Size gate for <c>EntityManager.SetComponentDataRaw</c>. Kept free of Unity / BepInEx so the
/// test project can compile this file without interop assemblies.
///
/// Live (evidence §32, 2026-09-03): a Follower blob write logged
/// <c>TypeSize=0 Marshal.SizeOf=32 sizeof=30</c> and the server died a tick later with Burst
/// <c>AppendRemovedComponentRecordError</c>. TypeSize 0 through this interop is not a trustworthy
/// write length (the same class of garbage as PathRequest TypeSize 4, evidence §11). Passing a
/// caller-chosen size larger than the ECS slot overruns the chunk. Typed
/// <c>GetComponentDataRawRW</c> field pokes, or a known-good <c>SetComponentData&lt;T&gt;</c>,
/// is the path that does not guess a blob.
///
/// Typed <c>Write&lt;T&gt;</c> is the same native call with <c>sizeof(T)</c>. TypeSize 0 is
/// allowed only for <see cref="TranslationTypeName"/> (evidence §§20-33: sizeof 12 used live as
/// a <c>SetComponentDataRaw</c> length). <see cref="FollowerTypeName"/> is <em>not</em> on that
/// allowlist: evidence §33 survived via a pointer overlay, not <c>SetComponentDataRaw(30)</c>.
/// <c>Write&lt;Follower&gt;</c> at TypeSize 0 is allowed only when <c>SizeInChunk</c> is known
/// and <c>&gt;= sizeof</c>; otherwise it is refused and the message names the overlay path.
/// Everyone else uses <c>WriteRaw</c> (refuses TypeSize 0) or a field poke.
/// </summary>
public static class RawWriteGuard
{
    /// <summary>
    /// <c>typeof(Unity.Transforms.Translation).FullName</c>. Trap 7: TypeSize reads 0 and
    /// a typed write of the interop sizeof still works (evidence §§20-33: unit Translation
    /// spawn, home pin, and walk). This is the only type whose <c>sizeof</c> has been
    /// live-logged as a <c>SetComponentDataRaw</c> length while TypeSize read 0.
    /// </summary>
    public const string TranslationTypeName = "Unity.Transforms.Translation";

    /// <summary>
    /// <c>typeof(ProjectM.Follower).FullName</c>. TypeSize 0, sizeof 30, Marshal 32
    /// (evidence §32). The path that survived is a <c>GetComponentDataRawRW</c> field poke
    /// at Followed @0 / ModeModifiable @24 / Stationary @28 (evidence §33 / step 35), not
    /// <c>SetComponentDataRaw</c> of 30 or 32 bytes. Named here so refuse messages can
    /// point at that overlay; not on <see cref="IsTypeSizeZeroWriteAllowlisted"/>.
    /// </summary>
    public const string FollowerTypeName = "ProjectM.Follower";

    /// <summary>
    /// Informational only since 2026-09-03: TypeSize 0 typed writes are allowed for every
    /// component (see <see cref="EnsureTypedWrite"/>). Kept so callers and tests that ask
    /// "was this the first live-verified TypeSize-0 write" keep compiling.
    /// </summary>
    public static bool IsTypeSizeZeroWriteAllowlisted(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return typeName.Equals(TranslationTypeName, StringComparison.Ordinal);
    }

    /// <summary>
    /// Throws when <paramref name="ecsTypeSize"/> cannot be used as a <c>SetComponentDataRaw</c>
    /// length for <paramref name="typeName"/>. Zero (or negative) TypeSize is garbage through
    /// this interop; a TypeSize smaller than <paramref name="requestedSize"/> would write past
    /// the slot. The exception names the type so a log line is enough to find the caller.
    /// </summary>
    public static void EnsureWritable(int ecsTypeSize, int requestedSize, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentException("Type name is required so a refused write can be attributed.", nameof(typeName));
        if (requestedSize <= 0)
        {
            throw new InvalidOperationException(
                $"Refusing SetComponentDataRaw of {typeName}: requested write size {requestedSize} is not a positive length.");
        }

        if (ecsTypeSize <= 0)
        {
            throw new InvalidOperationException(
                $"Refusing SetComponentDataRaw of {typeName}: TypeManager.GetTypeInfo.TypeSize is {ecsTypeSize} (requested {requestedSize}). " +
                "A zero TypeSize through this interop is not a trustworthy write length (evidence §32: Follower TypeSize=0, Marshal.SizeOf=32, sizeof=30, then a Burst AppendRemovedComponentRecordError).");
        }

        if (ecsTypeSize < requestedSize)
        {
            throw new InvalidOperationException(
                $"Refusing SetComponentDataRaw of {typeName}: TypeManager.GetTypeInfo.TypeSize {ecsTypeSize} is smaller than the requested write size {requestedSize}. " +
                "Writing that many bytes would overrun the ECS slot (evidence §11: PathRequest TypeSize=4 vs sizeof=82 / Marshal=84).");
        }
    }

    /// <summary>
    /// Gate for typed <c>Write&lt;T&gt;</c> (a <c>SetComponentDataRaw</c> of <c>sizeof(T)</c>).
    /// TypeSize 0 is the norm for game components through this interop, not an anomaly:
    /// Translation, LastTranslation, SpawnTransform, LifeTime and Follower all read 0 and typed
    /// writes of the interop <c>sizeof</c> are live-verified for every one of them (evidence
    /// sections 20-33; the 2026-09-03 allowlist attempt refused LastTranslation/SpawnTransform/
    /// LifeTime and broke the unit spike). What killed the server in section 32 was a raw blob
    /// of a guessed length (32 for a 30-byte type) plus a bare marker entity, so raw byte-count
    /// writes at TypeSize 0 stay refused in <see cref="EnsureWritable"/>. Typed writes at TypeSize 0
    /// use <c>sizeof(T)</c>; SizeInChunk is ignored because it is garbage in both directions (515 for Follower).
    /// Nonzero TypeSize must equal <paramref name="managedSize"/>.
    /// </summary>
    public static void EnsureTypedWrite(int ecsTypeSize, int managedSize, int sizeInChunk, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentException("Type name is required so a refused write can be attributed.", nameof(typeName));
        if (managedSize <= 0)
        {
            throw new InvalidOperationException(
                $"Refusing Write of {typeName}: managed size {managedSize} is not a positive length.");
        }

        if (ecsTypeSize == 0)
        {
            // SizeInChunk through this interop is garbage in both directions (PathRequest read
            // 525 at TypeSize 4, Follower read 515 at sizeof 30, evidence sections 11 and 37), so
            // it is not consulted at all: a small garbage value would refuse the live-verified
            // Translation write (R8 regression A). The typed sizeof is the write length here.
            _ = sizeInChunk;
            return;
        }

        if (ecsTypeSize < 0)
        {
            throw new InvalidOperationException(
                $"Refusing Write of {typeName}: TypeManager.GetTypeInfo.TypeSize is {ecsTypeSize}.");
        }

        if (ecsTypeSize != managedSize)
        {
            throw new InvalidOperationException(
                $"Refusing Write of {typeName}: TypeManager.GetTypeInfo.TypeSize {ecsTypeSize} does not match sizeof {managedSize} " +
                "(evidence section 11: PathRequest TypeSize=4 vs sizeof=82 / Marshal=84).");
        }
    }

    /// <summary>
    /// Memcpy budget for a <c>GetComponentDataRawRW</c> overlay pointer (ItemKeyTable, Follower
    /// field pokes). TypeSize 0 means "poke fields; do not memcpy a guessed sizeof". Never
    /// throws on TypeSize 0 — overlays need that pointer. This is the gate
    /// <c>Singletons.TryGetRawRW</c> routes through: callers that want to copy bytes must
    /// pass the returned budget to <see cref="EnsureOverlayMemcpy"/>.
    /// </summary>
    public static int OverlayMemcpyBudget(int ecsTypeSize) => ecsTypeSize > 0 ? ecsTypeSize : 0;

    /// <summary>
    /// Throws when a caller would memcpy <paramref name="copySize"/> bytes through an overlay
    /// pointer whose TypeSize is 0 or smaller than the copy. Field pokes (typed pointer at a
    /// known offset) do not call this.
    /// </summary>
    public static void EnsureOverlayMemcpy(int ecsTypeSize, int copySize, string typeName)
    {
        if (string.IsNullOrEmpty(typeName))
            throw new ArgumentException("Type name is required so a refused overlay copy can be attributed.", nameof(typeName));
        if (copySize <= 0)
        {
            throw new InvalidOperationException(
                $"Refusing overlay memcpy of {typeName}: copy size {copySize} is not a positive length.");
        }

        int budget = OverlayMemcpyBudget(ecsTypeSize);
        if (budget <= 0)
        {
            throw new InvalidOperationException(
                $"Refusing overlay memcpy of {typeName}: TypeSize is {ecsTypeSize} so there is no memcpy budget. " +
                "Poke fields through the pointer (ItemKeyTable, Follower @0/@24/@28); do not Buffer.MemoryCopy a guessed sizeof.");
        }

        if (copySize > budget)
        {
            throw new InvalidOperationException(
                $"Refusing overlay memcpy of {typeName}: copy size {copySize} exceeds TypeSize {budget}.");
        }
    }
}
