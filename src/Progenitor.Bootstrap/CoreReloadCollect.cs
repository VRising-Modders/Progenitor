using System;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;

namespace Progenitor.Bootstrap;

/// <summary>
/// Microsoft unloadability helpers for a collectible Core generation. Load/Install live
/// in <c>CoreLoader</c> (NoInlining there too); this type owns the unload half so unit
/// tests can assert <c>trackResurrection</c>, the Collect → WaitForPendingFinalizers
/// sequence, and that the ALC subclass nulls its fields before <c>Unload</c> without
/// referencing BepInEx.
///
/// <para><b>The slot clear is a parameter, not a caller's discipline</b> (job FLATCH4,
/// R47 finding 3). Bootstrap holds delegates that belong to the collectible generation —
/// Harmony forwards, both receive slots, the lifetime and tick adapters, the Ui dispatch,
/// key and capture slots, and the input mask's apply slot. Every one of them is a strong
/// reference into the ALC, and this project treats a reference that survives
/// <c>Unload</c> as a first-class defect, because the generation that does not collect
/// refuses the next swap. Until this job those clears were spread over the call sites and
/// the guarantee was a sentence: <c>ClearFailedInstallSlots</c>' own summary claimed to
/// null <i>every</i> such slot, and the mask slot — added by FLATCH3 in the same batch —
/// was not in it. The list that guarded the sentence was length-locked, which can only
/// catch an addition to the list and never an omission from it, so nothing failed.
/// The clear is now an argument every Core-ALC unload has to supply, and the one context
/// that legitimately needs none goes through <see cref="UnloadNeverInstalled"/>, whose
/// name is the justification. A path added later cannot forget what it cannot omit.</para>
///
/// <para>A throwing clear must not skip the <c>Unload</c>: a half-cleared generation that
/// stays loaded is strictly worse than a half-cleared one that goes away, and the swap
/// after it is refused either way. The exception is kept in
/// <see cref="LastClearError"/> for the caller to log rather than swallowed, because a
/// clear that threw is exactly the fact a failed collect has to be read against.</para>
/// </summary>
public static class CoreReloadCollect
{
    /// <summary>
    /// The exception the most recent clear threw, or <c>null</c> when it did not throw.
    /// Reset at the start of every unload here, so a caller reads it immediately after
    /// its own call and never inherits an older generation's answer. The production
    /// clear (<c>CoreLoader.ClearFailedInstallSlots</c>) guards every slot individually,
    /// so a non-null value here means something outside that per-slot net failed.
    /// </summary>
    public static Exception? LastClearError { get; private set; }

    /// <summary>
    /// Clear the Bootstrap slots that reference this generation, null the ALC's own
    /// fields, take a resurrection-tracking weak, then <c>Unload</c>. NoInlining so no
    /// stack slot of the caller is assumed to keep the context; the live cascade still
    /// yields a FrameTicker tick before the collect poll so <c>UnloadAlc</c> locals are
    /// gone too.
    /// </summary>
    /// <param name="clearCoreSlots">
    /// Required. Runs immediately before the unload and may run a second time for the same
    /// generation — the cascade already clears earlier in <c>NullRuntime</c> and the
    /// failed-install path clears before it releases pins — so it must be idempotent.
    /// </param>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference PrepareAndUnload(CoreLoadContext alc, Action clearCoreSlots)
    {
        if (alc == null) throw new ArgumentNullException(nameof(alc));
        if (clearCoreSlots == null) throw new ArgumentNullException(nameof(clearCoreSlots));
        RunClear(clearCoreSlots);
        alc.PrepareUnload();
        var weak = CoreReloadPolicy.CreateGenerationWeak(alc);
        alc.Unload();
        return weak;
    }

    /// <summary>
    /// Same as <see cref="PrepareAndUnload(CoreLoadContext, Action)"/> for a plain
    /// <see cref="AssemblyLoadContext"/> (tests, or a context that is not our subclass).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference RequestUnload(AssemblyLoadContext alc, Action clearCoreSlots)
    {
        if (alc == null) throw new ArgumentNullException(nameof(alc));
        if (clearCoreSlots == null) throw new ArgumentNullException(nameof(clearCoreSlots));
        if (alc is CoreLoadContext core)
            return PrepareAndUnload(core, clearCoreSlots);
        RunClear(clearCoreSlots);
        var weak = CoreReloadPolicy.CreateGenerationWeak(alc);
        alc.Unload();
        return weak;
    }

    /// <summary>
    /// The one door out for a context that never had <c>Runtime.Install</c> invoked in it
    /// and therefore never handed Bootstrap a delegate to hold: the throwaway ALC
    /// <c>CoreLoader.TryValidateCoreImage</c> loads a candidate image into to read its
    /// <c>Install</c> signature and protocol constant. It reflects over types and calls
    /// nothing, so there is no slot to clear and no pin to release.
    ///
    /// <para>This exists as a named method rather than as a null clear argument so that
    /// "no clear needed" is a claim someone had to write down, and so a test can assert
    /// how many call sites make it. A second caller appearing here is the review question,
    /// not a silent regression.</para>
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static WeakReference UnloadNeverInstalled(CoreLoadContext alc)
    {
        if (alc == null) throw new ArgumentNullException(nameof(alc));
        alc.PrepareUnload();
        var weak = CoreReloadPolicy.CreateGenerationWeak(alc);
        alc.Unload();
        return weak;
    }

    private static void RunClear(Action clearCoreSlots)
    {
        LastClearError = null;
        try { clearCoreSlots(); }
        catch (Exception e) { LastClearError = e; }
    }
}
