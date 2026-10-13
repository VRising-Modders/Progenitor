using System;
using System.Collections.Generic;

namespace Progenitor.Effects;

/// <summary>
/// Live sequence handles that need a late-frame hybrid write. Bootstrap owns the Unity
/// pump (<c>Camera.onPreCull</c> if it fires; otherwise FrameTicker LateUpdate after
/// PresentationSystemGroup) and invokes Core's LateFrame slot; this type is the slot
/// body. Never subscribe a Core method to a Unity event (evidence 101:
/// <c>Application.onBeforeRender</c> is stripped, and ConvertDelegate would pin the
/// collectible ALC).
/// </summary>
internal static class SequenceLateDriver
{
    internal const int VerdictAfterFrames = 3;
    internal const string VerdictLateFrame = "drawn by late-frame SetPosition";
    internal const string VerdictSequencer = "sequencer honored the marker";
    internal const string VerdictOwnedLine = "drawn by owned LineRenderer";
    internal const string VerdictOwnedGameMaterial = "drawn by owned LineRenderer with game material";
    internal const string VerdictGameLineBuffer = "drawn by game line via LineRendererPositionElement";

    /// <summary>
    /// EV12: the beam body is a copy of a vanilla two-point beam prefab that we
    /// <c>Instantiate</c>d ourselves, so the game's own material draws it and every
    /// write lands on an object we own (evidence 130).
    /// </summary>
    internal const string VerdictBorrowedGameLine = "drawn by a borrowed game line prefab";

    /// <summary>
    /// Default owned-line width in meters. Constant (no width curve). Evidence 119:
    /// copying the prefab's 0.53 m trail-sprite width rendered a folded sheet.
    /// </summary>
    internal const float OwnedLineDefaultWidth = 0.12f;

    /// <summary>Alias of <see cref="OwnedLineDefaultWidth"/> for older call sites.</summary>
    internal const float OwnedLineFallbackWidth = OwnedLineDefaultWidth;

    /// <summary>
    /// Soft blue used when the handle has not set a tint. Alpha 0.8 so additive
    /// HDRP Unlit reads as a beam, not an opaque ribbon.
    /// </summary>
    internal static readonly EffectColor OwnedLineDefaultTint = new(0.25f, 0.55f, 1f, 0.8f);

    /// <summary>
    /// <c>Shader.Find</c> order for the default owned-line material. First non-null
    /// wins. Live (evidence 35, 488): <c>HDRP/Unlit</c> and <c>Sprites/Default</c>
    /// resolve; the Hidden / Unlit/Color / legacy additive names do not.
    /// </summary>
    internal static readonly string[] OwnedLineShaderCandidates =
    {
        "HDRP/Unlit",
        "Hidden/HDRP/Unlit",
        "Sprites/Default",
        "Unlit/Color",
        "Legacy Shaders/Particles/Additive",
    };

    /// <summary>
    /// When <c>HDRP/Unlit</c> exists but <c>Shader.isSupported</c> is false (or the
    /// material would draw magenta without the transparent keywords), try these
    /// in order. Evidence 125.
    /// </summary>
    internal static readonly string[] OwnedLineFallbackShaders =
    {
        "Sprites/Default",
        "Universal Render Pipeline/Unlit",
        "Unlit/Color",
    };

    /// <summary>
    /// Once-per-handle owned-line style log. Allocates; call only when the line
    /// is first configured (R25 #12).
    /// </summary>
    internal static string FormatOwnedLineStyleLog(string shader, float width) =>
        "Effects: sequence owned line material=" + shader + " width=" + width.ToString("0.##");

    /// <summary>
    /// Once-per-handle owned-line Unity readback (the clone is never the drawable).
    /// Evidence 125: the previous "LineRenderer after write" line read the pooled
    /// clone, so a 0.12 m intent could hide a 1 m <c>startWidth</c>.
    /// </summary>
    internal static string FormatOwnedLineReadbackLog(
        float startWidth,
        float widthMultiplier,
        float lossyX,
        float lossyY,
        float lossyZ,
        string shader,
        bool isSupported,
        int renderQueue) =>
        "Effects: sequence owned line readback startWidth=" + startWidth.ToString("0.##")
        + " widthMultiplier=" + widthMultiplier.ToString("0.##")
        + " lossyScale=(" + lossyX.ToString("0.##") + "," + lossyY.ToString("0.##") + "," + lossyZ.ToString("0.##") + ")"
        + " shader=" + shader
        + " isSupported=" + isSupported
        + " renderQueue=" + renderQueue;

    /// <summary>Once-per-handle draw-path log after a GameRed/GameBlue copy or buffer write.</summary>
    internal static string FormatDrawPathLog(string path) =>
        "Effects: sequence beam " + path;

    /// <summary>Once-per-handle glow-halo log. Allocates; call only when the glow line is created.</summary>
    internal static string FormatGlowLineLog(float width, float alpha) =>
        "Effects: sequence glow line width=" + width.ToString("0.##") + " alpha=" + alpha.ToString("0.##");

    /// <summary>The copied game material already carries emission / HDR glow; no second line.</summary>
    internal const string GlowFromMaterialLog = "Effects: sequence glow comes from the copied material";

    /// <summary>Clone LineRenderer uses a trail-sprite shader; copying it folds the sheet (evidence 119).</summary>
    internal static string FormatTrailSpriteSkipLog(string shader) =>
        "Effects: sequence clone LineRenderer shader '" + shader
        + "' is a trail sprite; keeping owned unlit (evidence 119)";

    /// <summary>
    /// Exact log fragment after <see cref="VerdictOwnedLine"/>. The Cardinal hybrid
    /// LineRenderer is not the visible body (evidence 110: late SetPosition landed,
    /// user heard the beam, no pixels).
    /// </summary>
    internal const string OwnedLineNotDrawable =
        "sequencer line not drawable: HybridLineRenderer Refresh copies Positions into LineRenderer.SetPosition but Cardinal's visible body is the ParticleSystem/trail graph";

    /// <summary>
    /// How many inspect format helpers have run this process. Tests assert this does
    /// not grow after the first late write of a handle (R25 #12).
    /// </summary>
    internal static int InspectFormatCount;

    private static readonly List<IRuntimeObject> Live = new();

    internal static string DescribePins() => Live.Count > 0 ? "lateFrame=" + Live.Count : "";

    internal static void Watch(IRuntimeObject obj)
    {
        if (obj == null) return;
        for (int i = 0; i < Live.Count; i++)
        {
            if (ReferenceEquals(Live[i], obj))
                return;
        }

        Live.Add(obj);
        string driver = obj.DriverName;
        if (string.IsNullOrEmpty(driver)) driver = "(unbound)";
        try
        {
            Plugin.Logger?.LogInfo(
                "Effects: sequence late-frame watch seq=" + obj.SequenceEntityId
                + " driver=" + driver);
        }
        catch
        {
            /* */
        }
    }

    internal static void Forget(IRuntimeObject obj)
    {
        for (int i = Live.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(Live[i], obj))
                Live.RemoveAt(i);
        }
    }

    /// <summary>Dispose / RemoveAll / recycle: drop this object from the late-frame list.</summary>
    internal static void Unwatch(IRuntimeObject obj) => Forget(obj);

    internal static void Uninstall()
    {
        Live.Clear();
        InspectFormatCount = 0;
#if !PROGENITOR_TESTS
        SequenceDriverLookup.Uninstall();
        UnityEffectRuntime.ResetGameLinePrefabCache();
        UnityEffectRuntime.ResetCloneTintLog();
#endif
    }

    /// <summary>
    /// Once-per-handle hybrid Positions log. Allocates; call only from the first-write
    /// inspect branch (R25 #12).
    /// </summary>
    internal static string FormatHybridPositionsLog(string before, string after, bool stuck, bool sequencerMatched)
    {
        InspectFormatCount++;
        return "Effects: sequence hybrid Positions before=" + before
            + " after=" + after
            + " stuck=" + stuck
            + " sequencerMatched=" + sequencerMatched;
    }

    /// <summary>
    /// Once-per-handle first late-write log. Allocates; call only from the first-write
    /// inspect branch (R25 #12).
    /// </summary>
    internal static string FormatFirstWriteLog(string game, in EffectVec3 start, in EffectVec3 end, int frame)
    {
        InspectFormatCount++;
        return "Effects: sequence late-frame first write game=" + game
            + " ours=[" + start + "," + end + "] frame=" + frame;
    }

    /// <summary>
    /// Bootstrap LateFrame slot body. Writes hybrid endpoints only while a driver is
    /// bound; disposed / unbound objects are dropped or skipped.
    /// </summary>
    internal static void OnLateFrame()
    {
        try
        {
            for (int i = Live.Count - 1; i >= 0; i--)
            {
                var obj = Live[i];
                if (obj == null || !obj.Alive)
                {
                    Live.RemoveAt(i);
                    continue;
                }

                // Owned LineRenderer writes even before the pooled clone binds (the
                // default Cardinal draw path). Hybrid SetPosition still needs HasDriver.
                try { obj.DriveLateWrite(); }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning("Effects: sequence late-frame drive threw: " + e.Message);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("Effects: sequence LateFrame threw: " + e.Message);
        }
    }

#if PROGENITOR_TESTS
    internal static void TickForTests() => OnLateFrame();

    internal static int LiveCount => Live.Count;
#endif
}

/// <summary>
/// R26 finding 3 (EV12). A GameObject the sequencer handed us is <b>read-only</b>: it comes
/// out of <c>ParticlePool._PrefabToInstanceQueue</c> and goes back in
/// on <c>ReturnInstance</c>, so a <c>Renderer.material</c> instance, a <c>startColor</c>, a
/// <c>BeamEmitter.StartColor</c> or a <c>Destroy</c> of an instanced material leaks into every
/// later user of that clone — ours or the game's (evidence 125: the magenta pool).
/// Only objects this process created (a particle clone we <c>Instantiate</c>d, the owned
/// LineRenderer, a borrowed game-line prefab copy) may be written.
/// </summary>
internal static class CloneWritePolicy
{
    /// <summary>Logged once when a tint is skipped because the target is a pooled clone.</summary>
    internal const string PooledCloneReadOnly =
        "the sequencer clone is pooled and read-only; tint stays on objects we instantiated (R26 finding 3)";

    /// <summary>
    /// True only when <paramref name="weInstantiatedIt"/>. <paramref name="kind"/> is
    /// carried so the rule reads at the call site and so a future kind cannot opt itself
    /// in by accident: <see cref="AssetKind.Sequence"/> handles never own their driver.
    /// </summary>
    internal static bool AllowsMaterialWrites(AssetKind kind, bool weInstantiatedIt)
    {
        if (kind == AssetKind.Sequence) return false;
        return weInstantiatedIt;
    }

    /// <summary>Log line for a skipped tint on <paramref name="what"/>.</summary>
    internal static string FormatSkippedTintLog(string what) =>
        "Effects: tint skipped on '" + what + "': " + PooledCloneReadOnly;
}

/// <summary>What <see cref="DriverClaimPolicy"/> allows for one candidate clone.</summary>
internal enum DriverClaim
{
    /// <summary>Leave it alone. A vanilla beam already on screen lands here (R26 finding 7).</summary>
    None = 0,

    /// <summary>Created after our play frame and still parked: this is our sequence's clone.</summary>
    NewClone = 1,

    /// <summary>Already bound to a Progenitor handle; the sequencer reactivated it (EV6 pool reuse).</summary>
    PoolReuse = 2,
}

/// <summary>
/// R26 finding 7 (EV12). The fallback finder ran when the sequence entity had no managed
/// hybrid yet and its last branch was "newest hybrid of any kind", which hands over a vanilla
/// beam that was already playing: we reparent it, activate it and deactivate it mid-effect.
/// The authoritative bind is <c>SequenceDriverLookup.FromSequenceEntity</c>; this policy is
/// the only fallback and it never claims a clone that is both unknown to us and live.
/// </summary>
internal static class DriverClaimPolicy
{
    /// <summary>Logged once per handle when nothing was claimable and the poll will retry.</summary>
    internal const string NothingClaimable =
        "Effects: no claimable sequence driver (every hybrid clone was already on screen and unbound); staying unbound";

    /// <param name="seenBefore">The clone existed in the snapshot taken just before we played.</param>
    /// <param name="boundToAnyHandle">A Progenitor handle already holds this clone.</param>
    /// <param name="activeInHierarchy">The clone is currently drawing.</param>
    internal static DriverClaim Decide(bool seenBefore, bool boundToAnyHandle, bool activeInHierarchy)
    {
        if (boundToAnyHandle) return DriverClaim.PoolReuse;
        if (seenBefore) return DriverClaim.None;
        // Evidence 99: a fresh clone lands inactive and we SetActive it once. An unseen clone
        // that is already drawing belongs to something else that started in the same frame.
        return activeInHierarchy ? DriverClaim.None : DriverClaim.NewClone;
    }

    /// <summary>
    /// R28 finding 13 (EV16). The fallback finder may only run while the handle has never been
    /// recycled and still holds the play-frame snapshot.
    ///
    /// <para>R27 finding 11's fix made a handle <em>survive</em> the recycle, but
    /// <c>TryBindSequenceDriver</c> then fell through from <c>DropRecycledDriver()</c> to
    /// <c>FindFallback(_seenDrivers)</c>. That snapshot is captured once at play and never
    /// refreshed, and <c>DropRecycledDriver</c> has just removed the clone from the bound set —
    /// so <see cref="Decide"/> saw <c>seenBefore: false, bound: false, active: false</c> and
    /// returned <see cref="DriverClaim.NewClone"/> for the very clone the sequencer had just
    /// parked. The next <c>Play()</c> / <c>Tint</c> / <c>SetEndpoints</c> re-claimed a
    /// pool-owned clone, reparented and activated it, and <c>ParticlePool.GetInstance</c> then
    /// handed the same object to a vanilla effect that drew under our root until
    /// <c>DestroyRoot</c> took it — R26 finding 7's failure, re-reachable.</para>
    ///
    /// <para><see cref="Decide"/> is unchanged: an active clone that is not ours is still never
    /// claimed. This is the extra condition, and the production path also drops the stale
    /// snapshot on the recycle so there is nothing left to walk.</para>
    /// </summary>
    /// <param name="recycled">The sequencer has taken this handle's clone back at least once.</param>
    /// <param name="haveSnapshot">The play-frame snapshot is still held.</param>
    internal static bool MayUseFallback(bool recycled, bool haveSnapshot) => !recycled && haveSnapshot;

    /// <summary>Ordering between two allowed candidates: newest instance id wins, NewClone over PoolReuse.</summary>
    internal static bool Prefer(DriverClaim candidate, int candidateId, DriverClaim best, int bestId)
    {
        if (candidate == DriverClaim.None) return false;
        if (best == DriverClaim.None) return true;
        if (candidate != best) return candidate == DriverClaim.NewClone;
        return candidateId >= bestId;
    }
}

/// <summary>What a <see cref="DriverRecycleState.OnRecycled"/> transition asks the handle to do.</summary>
internal readonly struct RecycleAction
{
    internal RecycleAction(bool hideOwnedLine, bool hideGameLine, bool keepWatching, string? log)
    {
        HideOwnedLine = hideOwnedLine;
        HideGameLine = hideGameLine;
        KeepWatching = keepWatching;
        Log = log;
    }

    /// <summary>
    /// Always false. The owned <c>LineRenderer</c> is our own GameObject with our own material
    /// under our own root and nothing about it depends on the pooled clone (R27 finding 11).
    /// </summary>
    internal bool HideOwnedLine { get; }

    /// <summary>
    /// True on the first recycle: the borrowed prefab copy really is clone-derived, so its
    /// writes stop here.
    /// </summary>
    internal bool HideGameLine { get; }

    /// <summary>
    /// Always true. The handle stays on the late-frame list so the owned line keeps drawing
    /// from the handle's endpoints until the handle's own lifetime ends.
    /// </summary>
    internal bool KeepWatching { get; }

    /// <summary>The one line to write, or null when this recycle was already recorded.</summary>
    internal string? Log { get; }
}

/// <summary>
/// R26 finding 8 / R27 finding 11 (EV15). When the sequencer recycles the pooled clone the
/// owned LineRenderer is still drawing the last two points it was given. R26 offered two fixes
/// and named the first — "on recycle only null the driver, keep the handle watched so the owned
/// line keeps tracking". EV12 took the second and made it terminal: it disabled the owned line
/// and unwatched the handle, with no rebind path, so a 30-second tracked beam vanished the
/// moment the underlying game sequence ended (typically a second or two) and only
/// <c>Dispose</c> ever ran again.
///
/// <para>The transition now stops the <b>clone-related</b> writes only: the borrowed game line
/// is disabled, the driver reference is dropped, one line is logged, and the owned line and the
/// watch both survive.</para>
/// </summary>
internal struct DriverRecycleState
{
    /// <summary>Log line emitted on the first recycle of a handle.</summary>
    internal const string RecycledLog =
        "Effects: sequence driver recycled by the sequencer; clone writes stopped, owned line keeps drawing";

    /// <summary>True once the sequencer took the clone back.</summary>
    internal bool Recycled;

    /// <summary>True once the borrowed prefab copy has been disabled for this recycle.</summary>
    internal bool GameLineHidden;

    /// <summary>True once <see cref="RecycledLog"/> has been written for this handle.</summary>
    internal bool Logged;

    /// <summary>
    /// Record a recycle and say what the handle should do. The first call returns the log line
    /// and asks for the game line to be hidden; later calls are idempotent no-ops that still
    /// report <see cref="RecycleAction.KeepWatching"/>.
    /// </summary>
    internal RecycleAction OnRecycled()
    {
        if (Recycled)
            return new RecycleAction(hideOwnedLine: false, hideGameLine: false, keepWatching: true, log: null);
        Recycled = true;
        GameLineHidden = true;
        Logged = true;
        return new RecycleAction(hideOwnedLine: false, hideGameLine: true, keepWatching: true, RecycledLog);
    }
}

/// <summary>
/// R26 finding 9 (EV12). Pool bookkeeping compared Il2CppInterop wrappers with
/// <c>ReferenceEquals</c>; two wrappers can front the same native object, so the guard
/// "the parent is our own root" could read false and record <em>our root</em> as the pool
/// parent. <c>RestorePoolParent</c> would then park the game's clone under our root and
/// <c>DestroyRoot</c> would destroy it. Identity is <c>GetInstanceID()</c>, never a
/// reference compare.
/// </summary>
internal static class PoolParentPolicy
{
    /// <summary><c>GetInstanceID()</c> never returns 0 for a live object; 0 means "unknown".</summary>
    internal const int NoInstanceId = 0;

    /// <summary>Logged when the candidate pool parent turned out to be our own root.</summary>
    internal const string ParentIsOurRoot =
        "Effects: pooled clone was already under our root; taking ParticleLifetimeSystem._ParticleParent as the pool parent";

    /// <summary>True when the two ids name the same Unity object (wrapper identity is not usable).</summary>
    internal static bool SameObject(int a, int b) => a != NoInstanceId && a == b;

    /// <summary>
    /// True when <paramref name="parentId"/> may be recorded as the clone's pool parent.
    /// Our own root never can: parking a pooled clone there hands it to <c>DestroyRoot</c>.
    /// </summary>
    internal static bool CanRecordPoolParent(int parentId, int rootId) =>
        parentId != NoInstanceId && !SameObject(parentId, rootId);

    /// <summary>
    /// The sequencer took the clone back when it sits under the recorded pool parent again
    /// and is no longer drawing. Ids, not wrappers.
    /// </summary>
    internal static bool IsRecycledParent(int parentId, int poolParentId, int rootId, bool activeInHierarchy)
    {
        if (activeInHierarchy) return false;
        if (SameObject(parentId, rootId)) return false;
        return SameObject(parentId, poolParentId);
    }

    /// <summary>Logged once per handle when the clone's parent could not be read.</summary>
    internal const string OwnershipUnprovable =
        "Effects: sequence clone parent unreadable; clone writes skipped this frame (cannot prove the clone is still ours)";

    /// <summary>
    /// Job FEFF2. Who owns the pooled clone right now, decided from ids only.
    ///
    /// <para><b>Why this exists.</b> <see cref="IsRecycledParent"/> answers the question by
    /// inference: it requires the clone to be observed <em>parked</em> (<c>!activeInHierarchy</c>)
    /// and sitting under the recorded pool parent. That is a negative test on a transient state,
    /// and its correctness rests on a claim about the body of <c>ParticlePool.ReturnInstance</c>
    /// that the game's metadata does not show — the method is listed, but its body is native
    /// and not exported. <c>BindDriverGo</c>, by contrast, reparents the clone under <em>our</em>
    /// root, and the take/return path reparents it under
    /// <c>ParticleLifetimeSystem._ParticleParent</c>. "The parent is still our
    /// root" is therefore a <b>positive</b> ownership proof maintained by the game's own
    /// reparenting, and it needs nothing from <c>activeInHierarchy</c>.</para>
    ///
    /// <para><b>Fail safe.</b> Writing endpoints into a <c>LineRenderer</c> we no longer own is
    /// visible corruption in the game's or another mod's effect, so evidence we could not read
    /// means <see cref="DriverOwnership.Unprovable"/> — skip the clone write — and never
    /// "probably still ours". <c>Unprovable</c> is deliberately not <c>Recycled</c>: the recycle
    /// transition is terminal for the borrowed line (R27 finding 11), and a transient interop
    /// read failure must not take it.</para>
    /// </summary>
    /// <param name="parentRead">False when reading <c>transform.parent</c> threw.</param>
    /// <param name="parentId">The clone's current parent, or <see cref="NoInstanceId"/> when it has none.</param>
    /// <param name="poolParentId">The pool parent recorded at bind, or <see cref="NoInstanceId"/>.</param>
    /// <param name="rootId">Our own effect root.</param>
    /// <param name="boundUnderRoot">
    /// True when the bind read the parent back and saw our root, i.e. we hold the positive proof.
    /// False when the reparent never happened or could not be verified, in which case the older
    /// inference is kept rather than silently refusing a write that used to land.
    /// </param>
    internal static DriverOwnership DecideOwnership(
        bool parentRead,
        int parentId,
        int poolParentId,
        int rootId,
        bool boundUnderRoot,
        bool activeSelf,
        bool activeInHierarchy)
    {
        // The one piece of evidence the whole question turns on.
        if (!parentRead) return DriverOwnership.Unprovable;

        // Job FBUFF17 (R45 finding 30). NoInstanceId is not "no parent": the caller writes it
        // for a null Transform, a null Transform.gameObject AND a GetInstanceID() that threw,
        // so it is an unknown, not an observation. Treating it as a foreign parent made the
        // terminal Recycled transition fire on a read failure — and a null parent is no evidence
        // the sequencer took the clone back either, since ParticlePool.ReturnInstance
        // *reparents* under _ParticleParent, which is the whole basis of the
        // positive proof below. This is the mirror image of the rootId check three lines down
        // and gets the same answer.
        if (parentId == NoInstanceId) return DriverOwnership.Unprovable;

        if (boundUnderRoot)
        {
            // Our root's own id went unknown: we cannot compare, so we cannot claim it.
            if (rootId == NoInstanceId) return DriverOwnership.Unprovable;
            return SameObject(parentId, rootId) ? DriverOwnership.Ours : DriverOwnership.Recycled;
        }

        if (SameObject(parentId, rootId)) return DriverOwnership.Ours;
        if (IsRecycledParent(parentId, poolParentId, rootId, activeInHierarchy))
            return DriverOwnership.Recycled;
        return !activeSelf && !activeInHierarchy ? DriverOwnership.Recycled : DriverOwnership.Ours;
    }
}

/// <summary>
/// What we can prove about the pooled clone at a late write (job FEFF2).
/// </summary>
internal enum DriverOwnership
{
    /// <summary>The clone is still parented where we put it: the endpoint write may land.</summary>
    Ours,

    /// <summary>The sequencer took the clone back. Terminal for the borrowed game line.</summary>
    Recycled,

    /// <summary>
    /// Ownership could not be established this frame. Do not write, do not drop the bind.
    /// </summary>
    Unprovable,
}
