using System;

namespace Progenitor.Effects;

/// <summary>
/// Which Unity.Transforms component a marker write targets. Split so a LocalToWorld
/// failure (evidence 99) does not stop Translation / Rotation on the same entity.
/// </summary>
internal enum TransformWriteKind
{
    Translation = 0,
    Rotation = 1,
    LocalToWorld = 2,
}

/// <summary>
/// Marker transform writes cannot go through <c>Entity.Write&lt;T&gt;</c> /
/// <c>SetComponentDataRaw</c>: TypeManager TypeSize for
/// <c>Unity.Transforms.LocalToWorld</c> reads 4 against sizeof 64 (evidence 99; the
/// same untrustworthy-value class as PathRequest TypeSize 4, evidence 11), so
/// <c>RawWriteGuard.EnsureTypedWrite</c> correctly refuses. The Sample's known-good
/// path for these types is the interop generic <c>EntityManager.SetComponentData&lt;T&gt;</c>
/// (it already reads <c>LocalToWorld</c> that way). A failed write is logged once per
/// handle and not retried — 4364 identical warnings is not a retry policy.
/// </summary>
internal static class TransformWritePath
{
    /// <summary><c>typeof(Unity.Transforms.Translation).FullName</c>.</summary>
    public const string TranslationTypeName = "Unity.Transforms.Translation";

    /// <summary><c>typeof(Unity.Transforms.Rotation).FullName</c>.</summary>
    public const string RotationTypeName = "Unity.Transforms.Rotation";

    /// <summary><c>typeof(Unity.Transforms.LocalToWorld).FullName</c>.</summary>
    public const string LocalToWorldTypeName = "Unity.Transforms.LocalToWorld";

    /// <summary>
    /// Log token / test assertion for the live write. Not <c>Write&lt;T&gt;</c>.
    /// </summary>
    public const string GenericSetComponentData = "SetComponentData";

    /// <summary>
    /// True for the Unity.Transforms types whose interop TypeSize is untrustworthy
    /// (or unproven) and must use generic <c>SetComponentData&lt;T&gt;</c>.
    /// </summary>
    public static bool UseGenericSetComponentData(string? typeName)
    {
        if (string.IsNullOrEmpty(typeName)) return false;
        return typeName.Equals(TranslationTypeName, StringComparison.Ordinal)
            || typeName.Equals(RotationTypeName, StringComparison.Ordinal)
            || typeName.Equals(LocalToWorldTypeName, StringComparison.Ordinal)
            || typeName.Equals("Translation", StringComparison.Ordinal)
            || typeName.Equals("Rotation", StringComparison.Ordinal)
            || typeName.Equals("LocalToWorld", StringComparison.Ordinal);
    }

    /// <summary>
    /// After one failure on a handle, further writes of that component are skipped so
    /// the log is not one warning per frame (evidence 99).
    /// </summary>
    public static bool ShouldAttemptWrite(bool alreadyFailedThisHandle) => !alreadyFailedThisHandle;
}

/// <summary>
/// Per-handle transform-write bookkeeping. Shared by the Unity runtime and the test fake
/// so a refused LocalToWorld is one warning, not 4364.
/// </summary>
internal sealed class TransformWriteState
{
    public bool TranslationFailed;
    public bool RotationFailed;
    public bool LocalToWorldFailed;
    public int WarningCount;
    public bool AnySucceeded;

    public bool ShouldAttempt(TransformWriteKind kind) => kind switch
    {
        TransformWriteKind.Translation => TransformWritePath.ShouldAttemptWrite(TranslationFailed),
        TransformWriteKind.Rotation => TransformWritePath.ShouldAttemptWrite(RotationFailed),
        TransformWriteKind.LocalToWorld => TransformWritePath.ShouldAttemptWrite(LocalToWorldFailed),
        _ => false,
    };

    public void MarkFailed(TransformWriteKind kind)
    {
        switch (kind)
        {
            case TransformWriteKind.Translation:
                if (TranslationFailed) return;
                TranslationFailed = true;
                break;
            case TransformWriteKind.Rotation:
                if (RotationFailed) return;
                RotationFailed = true;
                break;
            case TransformWriteKind.LocalToWorld:
                if (LocalToWorldFailed) return;
                LocalToWorldFailed = true;
                break;
            default:
                return;
        }

        WarningCount++;
    }

    public void MarkSucceeded() => AnySucceeded = true;
}
