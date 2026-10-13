using System;
using System.Reflection;

namespace Progenitor.Patches;

/// <summary>
/// Reading and writing a slot body's <c>__args</c> (job FHARM7, R41).
///
/// <para><b>Why this is a type and not three private helpers.</b> A slot body is handed
/// <c>__args</c> <em>positionally</em>, so the parameter-name binding Harmony does for a typed
/// patch method has to happen at registration time instead — and every slot-routed adapter in
/// Core does the same three things: find the index of a named parameter, read that slot as a
/// value type, and (for a by-ref parameter) write the changed value back. That was written
/// privately inside <c>GodMode</c>, where it cannot be tested: <c>GodMode.cs</c> references
/// <c>ProjectM</c> and is not linked into the game-free test project, so R41 correctly noted
/// that its fail-closed claim — "a missing parameter name refuses the group rather than reading
/// an argument by position" — had no test at all. Here it does.</para>
///
/// <para><b>Fail closed means fail open at the call site.</b> <see cref="TryArg{T}"/> returning
/// false is the signal to run the original unpatched: a guard that cannot read its argument must
/// not guess, and must not break the game's code path either. A registration-time
/// <see cref="PatchRegistry.ArgIndex"/> of -1 is the stronger failure — refuse the patch
/// outright, before anything is applied.</para>
///
/// <para><b>The by-ref write-back is still unmeasured on IL2CPP.</b> Harmony propagates a
/// <c>ref</c> parameter through <c>__args</c>; that is its documented managed contract and it is
/// what <see cref="WriteBack{T}"/> relies on. It has not been measured against
/// <c>Il2CppDetourMethodPatcher</c> — see <c>harmony-patches.md</c> section 8.7 "Still owed".</para>
/// </summary>
public static class SlotArgs
{
    /// <summary>
    /// Read <paramref name="args"/>[<paramref name="index"/>] as <typeparamref name="T"/>. False
    /// — with <paramref name="value"/> left at default — when the array is null, the index is out
    /// of range or negative, or the boxed argument is some other type. False is "do not act",
    /// never "act on a default".
    /// </summary>
    public static bool TryArg<T>(object?[]? args, int index, out T value)
        where T : struct
    {
        value = default;
        if (args == null || index < 0 || index >= args.Length) return false;
        if (args[index] is not T typed) return false;
        value = typed;
        return true;
    }

    /// <summary>
    /// Write <paramref name="value"/> back into <paramref name="args"/>[<paramref name="index"/>],
    /// which is how Harmony propagates a <c>ref</c> parameter out of a prefix. False when the
    /// index does not address the array, so a caller can tell a dropped clamp from an applied one.
    /// </summary>
    public static bool WriteBack<T>(object?[]? args, int index, T value)
        where T : struct
    {
        if (args == null || index < 0 || index >= args.Length) return false;
        args[index] = value;
        return true;
    }

    /// <summary>
    /// The warning a call site logs when <see cref="PatchRegistry.ArgIndex"/> came back -1: name
    /// the method and the parameter, and say that the site is refused rather than read by
    /// position. <paramref name="consequence"/> is what the caller loses ("the whole kill-path
    /// group is refused", "this seam is left unpatched").
    /// </summary>
    public static string FormatMissingParameter(string? target, string? name, string? consequence) =>
        "patches: " + Safe(target) + " has no parameter named '" + Safe(name) + "' on this build, "
        + "so the slot body cannot bind it by name; " + Safe(consequence) + " rather than reading "
        + "an argument by a guessed position. Check the interop signature against the game's metadata.";

    /// <summary>
    /// The warning a call site logs when <see cref="TryArg{T}"/> came back false at run time: the
    /// argument was not the type the body expects, so the original is allowed through.
    /// </summary>
    public static string FormatWrongArgumentType(string? site, string? name, int index, object? actual, Type expected) =>
        "patches: " + Safe(site) + " argument '" + Safe(name) + "' at position " + index + " was "
        + (actual?.GetType().FullName ?? "null") + ", not " + (expected?.FullName ?? "?")
        + "; the original is allowed through unmodified.";

    /// <summary>
    /// Convenience for a call site that wants both halves at once: resolve by name and read.
    /// False when the name is not on <paramref name="target"/> or the value is the wrong type.
    /// </summary>
    public static bool TryNamedArg<T>(MethodBase? target, object?[]? args, string name, out T value)
        where T : struct =>
        TryArg(args, PatchRegistry.ArgIndex(target, name), out value);

    private static string Safe(string? s) => string.IsNullOrEmpty(s) ? "?" : s!;
}
