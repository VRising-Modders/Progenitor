using System;
using System.IO;
using System.Threading;

namespace Progenitor.Bootstrap;

/// <summary>
/// Every file operation a promote performs, behind one seam so the production sequence in
/// <see cref="PromoteSequence.Promote"/> can be driven without a game, without BepInEx and —
/// the point of the seam — by a fake that changes a file's contents <em>between</em> the gate
/// and the rotate. R32 finding 4 is exactly that swap, and a test cannot stage it against
/// <see cref="File"/> reliably (it would have to win a real race); against this interface it is
/// deterministic.
/// </summary>
public interface IPromoteFileSystem
{
    bool Exists(string path);
    void CreateDirectory(string path);
    void Delete(string path);
    void Move(string source, string destination);

    /// <summary><c>File.Replace</c>: destination is overwritten, its old contents land in backup.</summary>
    void Replace(string source, string destination, string backup);

    byte[] ReadAllBytes(string path);

    /// <summary>
    /// Create <paramref name="path"/> with <c>FileMode.CreateNew</c> and <c>FileShare.None</c>,
    /// write <paramref name="bytes"/>, close. Throws when the path already exists — a
    /// pre-created file at a name we chose is itself the attack, so it must never be reused.
    /// </summary>
    void WriteNewExclusive(string path, byte[] bytes);
}

/// <summary><see cref="File"/> and <see cref="Directory"/>. The only implementation production uses.</summary>
public sealed class RealPromoteFileSystem : IPromoteFileSystem
{
    public static readonly RealPromoteFileSystem Instance = new();

    public bool Exists(string path) => File.Exists(path);

    public void CreateDirectory(string path) => Directory.CreateDirectory(path);

    public void Delete(string path) => File.Delete(path);

    public void Move(string source, string destination) => File.Move(source, destination);

    public void Replace(string source, string destination, string backup) =>
        File.Replace(source, destination, backup);

    public byte[] ReadAllBytes(string path) => File.ReadAllBytes(path);

    public void WriteNewExclusive(string path, byte[] bytes)
    {
        // CreateNew: fail rather than reuse a file somebody else made at this name.
        // FileShare.None: nothing else may open it for read or write while we hold it.
        using var stream = new FileStream(
            path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.None);
        stream.Write(bytes, 0, bytes.Length);
        stream.Flush(true);
    }
}

/// <summary>Which of the two gates a promote runs, and therefore which refusal line it prints.</summary>
public enum PromoteKind
{
    Bootstrap,
    Core,
}

/// <summary>How a promote ended. Anything but <see cref="Promoted"/> left the live image usable.</summary>
public enum PromoteStatus
{
    /// <summary>Could not read <c>.next</c>. Nothing was touched.</summary>
    ReadFailed,

    /// <summary>The allowlist refused the hashed bytes.</summary>
    Refused,

    /// <summary>Listed, but not a usable image (PE / identity / protocol).</summary>
    Rejected,

    /// <summary>
    /// The rotate happened and the destination did <em>not</em> hash to the gated bytes.
    /// The previous image was restored. This is R32 finding 4's detection.
    /// </summary>
    VerifyFailed,

    /// <summary>
    /// The rotate happened and the destination could not be <em>read back</em> at all, so no
    /// hash was ever compared. R33 finding 10: this is a busy disk, not a swap, and the two must
    /// not share a log line. The just-rotated image is <em>kept</em> (it is the image the
    /// allowlist approved, as far as anything here knows), <c>.next</c> is left in place, and the
    /// verify runs again on the next start.
    ///
    /// <para><b>R34 finding 13.</b> Only a failure this sequence can itself classify as a
    /// transient sharing/lock condition — and only after
    /// <see cref="PromoteSequence.VerifyReadAttempts"/> bounded in-process attempts — reaches this
    /// status. Every other read failure is <see cref="VerifyFailed"/> and restores, because "we
    /// could not check" is not "we checked and it was fine".</para>
    /// </summary>
    VerifyUnreadable,

    /// <summary>The destination was re-read and hashes to exactly the bytes the allowlist approved.</summary>
    Promoted,
}

/// <summary>What the caller must decide before the sequence runs; the sequence decides nothing about layout.</summary>
public sealed class PromoteRequest
{
    public PromoteRequest(string currentPath, string nextPath, string label, PromoteKind kind)
    {
        CurrentPath = currentPath;
        NextPath = nextPath;
        Label = label;
        Kind = kind;
    }

    public string CurrentPath { get; }
    public string NextPath { get; }
    public string Label { get; }
    public PromoteKind Kind { get; }

    /// <summary>Name the allowlist row is matched against. Defaults to the live file's name.</summary>
    public string? RowName { get; set; }

    /// <summary>Where the outgoing live image is preserved, and where a failed verify restores from.</summary>
    public string BackupPath { get; set; } = "";

    /// <summary>Folder the exclusive temp is created in. Empty means the live file's folder.</summary>
    public string StagingDirectory { get; set; } = "";

    /// <summary><c>File.Replace</c> instead of move-aside + move-in (Core's atomic rotate).</summary>
    public bool UseReplace { get; set; }

    /// <summary>Bootstrap deletes <c>.prev</c> once the rotate verified; Core keeps it until Install proves N+1.</summary>
    public bool DeleteBackupAfterPromote { get; set; }

    /// <summary>Full path a refused <c>.next</c> is moved to, or null when the caller quarantines itself.</summary>
    public string? QuarantinePath { get; set; }
}

/// <summary>Outcome of one promote, including everything the caller needs to log.</summary>
public sealed class PromoteResult
{
    public PromoteStatus Status { get; init; }
    public string Error { get; init; } = "";
    public AllowDecision Decision { get; init; }

    /// <summary>SHA-256 the allowlist decided on, and the hash the destination was checked against.</summary>
    public string GatedHash { get; init; } = "";

    /// <summary>What the destination actually hashed to after the rotate. Empty until the verify runs.</summary>
    public string PromotedHash { get; init; } = "";

    /// <summary>Where <c>.next</c> was moved to, when this sequence quarantined it.</summary>
    public string? QuarantinedAs { get; init; }

    /// <summary>True when a failed verify put the previous image back at the live path.</summary>
    public bool Restored { get; init; }

    public bool Promoted => Status == PromoteStatus.Promoted;
}

/// <summary>
/// The one gated promote sequence, shared by <c>SelfUpdate</c>'s Bootstrap and Core paths.
///
/// <para><b>R32 finding 4.</b> Both paths used to hash a byte array and then rotate a
/// <em>file</em> that had been sitting at <c>CoreImagePolicy.PromoteTempPath(dll)</c> — a
/// compile-time constant in the same attacker-writable folder as the <c>.next</c> the gate
/// exists to distrust. The bytes that were hashed and the bytes that landed on the live dll
/// were different objects, with a whole <c>WriteAllBytes</c> plus a metadata open between them,
/// reached deterministically on every process start and every quit. A process spinning on that
/// constant path promoted its own image through the gate's approval of somebody else's.</para>
///
/// <para>Three changes, in increasing order of how much they actually buy:</para>
/// <list type="number">
///   <item>the temp name is random per promote, so there is no path to spin on;</item>
///   <item>it is created <c>CreateNew</c> + <c>FileShare.None</c> and closed before the rotate,
///     so a pre-created or concurrently-held file fails the promote instead of being reused;</item>
///   <item><b>the destination is re-read after the rotate and hashed against the gated hash.</b>
///     A mismatch restores the previous image from <see cref="PromoteRequest.BackupPath"/> and
///     reports <see cref="PromoteStatus.VerifyFailed"/> at error level.</item>
/// </list>
///
/// <para><b>R33 findings 9 and 10.</b> Two things about the failure half of (3) were wrong.
/// The restore deleted the live file before moving the backup back, so the rollback that exists
/// to guarantee a live image could destroy the only one — see <c>RestoreFrom</c>, which now does
/// an atomic <c>Replace</c> and keeps the backup until the live file is confirmed. And a verify
/// read that <em>threw</em> was folded into the mismatch branch, so a busy file reported the
/// swap attack, rolled back a legitimate promote, and did it again on every start because
/// <c>.next</c> was never consumed. The two are now separate statuses with the classification in
/// the log line: <see cref="PromoteStatus.VerifyUnreadable"/> (<c>class=io-error</c>, keep the
/// image, warn, verify again next start) and <see cref="PromoteStatus.VerifyFailed"/>
/// (<c>class=hash-mismatch</c>, restore, error).</para>
///
/// <para><b>R34 findings 5, 11 and 13.</b> Three more edges on the failure half.</para>
/// <list type="number">
///   <item><b>Finding 5 — the park path.</b> The restore parked the rejected image at the
///     compile-time constant <c>&lt;live&gt;.rejected.tmp</c>: exactly the "name an attacker can
///     sit on" the promote temp was randomized to avoid. A held file there made <c>Replace</c>
///     throw and the fallback bail, so <c>Restored = false</c> and the unapproved image stayed
///     live — permanently, because the same check refused every later restore too. The park name
///     is now random per restore (<see cref="NewRejectedName"/>), and parking is a convenience,
///     never a precondition: if the aside move cannot run, the rejected image is <b>deleted</b>
///     and the backup moved in, with <see cref="FormatParkFailed"/> appended to the error line.
///     A failure to park must never convert a detected swap into a kept swap.</item>
///   <item><b>Finding 11 — the restore ran when nothing moved.</b> <c>Rotate</c>'s first
///     statement is a cleanup <c>Delete(backup)</c>, and Windows will not delete a file with a
///     live image section — the very thing that leaves a stale <c>.prev</c> behind. That delete
///     throwing used to reach the unconditional catch, which restored the <em>old</em> image over
///     a freshly promoted good one at shutdown. <c>Rotate</c> now reports whether it mutated the
///     live path, and <c>RestoreFrom</c> runs only when it did.</item>
///   <item><b>Finding 13 — the io-error class was attacker-selectable.</b> "Did the read throw"
///     is a signal the adversary owns: hold the live file <c>FileShare.None</c> and a detected
///     swap degrades from restore-plus-error to keep-plus-warning. The read is now retried
///     <see cref="VerifyReadAttempts"/> times, and only a failure this sequence can positively
///     classify as a sharing or lock violation (<see cref="IsTransientSharingFailure"/>) is
///     <c>class=io-error</c>. Anything else counts as a failed verify and restores. The residual
///     is real and stated in <c>threat-model.md</c> §15.7: a holder can still produce a genuine
///     sharing violation, so the transient branch is narrowed, not closed.</item>
/// </list>
///
/// <para><b>Why not an ACL'd private staging directory instead</b> (the other option R32 finding 4
/// offers, as <c>DevGate.RestrictToCurrentUser</c> does for the Dev token). That helper restricts
/// a file to the <em>current user</em>, and this project's threat model (research
/// <c>threat-model.md</c> §15) is a local process running as that same user — it can write
/// <c>BepInEx/plugins</c> because the operator can. Such a process can also open, or simply
/// re-ACL, anything owned by the account it runs as, so a same-user DACL is not a boundary here;
/// it would read as protection without being any. The re-verify needs no ACL support, works on
/// any file system, and detects the swap rather than hoping to prevent it. Hence (3) is the fix
/// and the staging directory is left as a caller option (<see cref="PromoteRequest.StagingDirectory"/>)
/// rather than a claim.</para>
///
/// <para><b>Residual window, honestly.</b> The verify closes the promote as an attack
/// <em>amplifier</em>; it does not make the live file tamper-proof. An attacker who overwrites
/// the destination after the verify read has done nothing the promote enabled — they could
/// overwrite <c>Progenitor.Bootstrap.dll</c> at any other moment just as well, and for Bootstrap
/// specifically nothing re-checks the file at chainload. What is gone is the case the finding is
/// about: the gate approving image A while image B lands, so that a refused image reaches disk
/// with the allowlist's blessing. There is also a smaller window between the rotate and the
/// verify read in which a swapped-in image is momentarily live; it is detected and rolled back,
/// but a process that mapped the file in that instant kept the bad copy. Both are recorded in
/// <c>threat-model.md</c> §15.</para>
/// </summary>
public static class PromoteSequence
{
    /// <summary>Suffix every promote temp carries, after its random component.</summary>
    public const string TempSuffix = ".promote.tmp";

    /// <summary>
    /// A temp file name for one promote: the live file's name, a fresh GUID, and
    /// <see cref="TempSuffix"/>. Random on purpose — the old constant
    /// <c>CoreImagePolicy.PromoteTempPath</c> was a name an attacker could sit on.
    /// </summary>
    public static string NewTempName(string liveFileName) =>
        (string.IsNullOrEmpty(liveFileName) ? "promote" : liveFileName)
        + "." + Guid.NewGuid().ToString("N") + TempSuffix;

    /// <summary>Classification token every verify line carries, so a log reader can tell the two apart.</summary>
    public const string ClassMismatch = "class=hash-mismatch";

    /// <summary>Classification token for a verify that never got a hash to compare (R33 finding 10).</summary>
    public const string ClassIoError = "class=io-error";

    public static string FormatVerifyFailed(string label, string fileName, string expected, string actual) =>
        (label ?? "") + ": PROMOTE VERIFY FAILED (" + ClassMismatch + ") — " + (fileName ?? "")
        + " hashes to " + Short(actual) + " but the allowlist approved " + Short(expected)
        + ". The staged image was swapped after it was gated; the previous image has been restored.";

    public static string FormatRestoreFailed(string label, string fileName, string backupName, string error) =>
        (label ?? "") + ": PROMOTE VERIFY FAILED (" + ClassMismatch + ") for " + (fileName ?? "")
        + " and the restore from " + (backupName ?? "") + " also failed: " + (error ?? "")
        + ". The live file may be an image the allowlist never approved.";

    /// <summary>
    /// The verify read could not be completed and the failure is <em>not</em> one this sequence
    /// can call transient (R34 finding 13). An image that was never read back was never verified,
    /// and the promote's contract is that a verified image is what stays — so this restores and
    /// reports at <c>class=hash-mismatch</c> like any other failed verify.
    /// </summary>
    public static string FormatVerifyUnverifiable(string label, string fileName, string error) =>
        (label ?? "") + ": PROMOTE VERIFY FAILED (" + ClassMismatch + ") — " + (fileName ?? "")
        + " could not be read back after the rotate, and the failure is not a sharing or lock "
        + "violation this sequence can call transient: " + (error ?? "")
        + ". An image that was never read back was never verified; the previous image has been restored.";

    /// <summary>
    /// R34 finding 5. The rejected image could not be parked for evidence, so it was deleted and
    /// the backup moved in anyway. Appended to the error line the caller logs; a park is evidence,
    /// never a reason to leave an unverified image live.
    /// </summary>
    public static string FormatParkFailed(string fileName) =>
        "The rejected " + (fileName ?? "") + " could not be parked aside for evidence and was deleted; "
        + "the live file is the backup.";

    /// <summary>
    /// R33 finding 10. The verify read threw, so nothing was compared: this is an I/O condition
    /// (a scanner or indexer still holding the file we closed microseconds ago is the likely
    /// one on Windows), not the swap <see cref="FormatVerifyFailed"/> reports. Warning, not
    /// error; the rotated image stays; the next start verifies it again. R34 finding 13: only
    /// reached after <see cref="VerifyReadAttempts"/> attempts and only for a failure
    /// <see cref="IsTransientSharingFailure"/> recognizes.
    /// </summary>
    public static string FormatVerifyUnreadable(string label, string fileName, string error) =>
        (label ?? "") + ": promote verify could not read " + (fileName ?? "") + " after the rotate ("
        + ClassIoError + "): " + (error ?? "")
        + ". This is not a swap — no hash was compared. The promoted image is kept and the verify "
        + "is retried on the next start.";

    /// <summary>How many times the verify re-read is attempted before the failure is classified.</summary>
    public const int VerifyReadAttempts = 3;

    /// <summary>Base backoff between verify read attempts; attempt N waits N times this.</summary>
    public const int VerifyRetryDelayMs = 40;

    // Win32 error codes, as they appear in the low word of an IOException's HRESULT (0x8007xxxx).
    private const int ErrorSharingViolation = 32;
    private const int ErrorLockViolation = 33;

    /// <summary>
    /// R34 finding 13. The one read failure the sequence is willing to call "the disk was busy"
    /// rather than "we could not verify": a Win32 sharing or lock violation, the shape an
    /// anti-virus scanner or indexer holding the file produces. Deliberately narrow — the message
    /// text is not consulted (it is localized and unstable), only the HRESULT, and
    /// <see cref="FileNotFoundException"/> / <see cref="DirectoryNotFoundException"/> are excluded
    /// even though they derive from <see cref="IOException"/>: a live file that has vanished
    /// between the rotate and the read is not a transient condition, it is a missing image.
    /// </summary>
    public static bool IsTransientSharingFailure(Exception? e)
    {
        if (e is not IOException io) return false;
        if (e is FileNotFoundException || e is DirectoryNotFoundException) return false;

        int hr = io.HResult;
        if ((hr & unchecked((int)0xFFFF0000)) != unchecked((int)0x80070000)) return false;

        int code = hr & 0xFFFF;
        return code == ErrorSharingViolation || code == ErrorLockViolation;
    }

    private static void DefaultVerifyPause(int attempt) => Thread.Sleep(VerifyRetryDelayMs * attempt);

    private static string Append(string message, string note) =>
        string.IsNullOrEmpty(note) ? message : message + " " + note;

    private static string Short(string? hex) =>
        string.IsNullOrEmpty(hex) ? "(none)" : (hex!.Length <= 16 ? hex : hex.Substring(0, 16) + "...");

    /// <summary>
    /// Read once, gate, stage exclusively, rotate, re-read, verify, and on mismatch restore.
    /// </summary>
    /// <param name="fs">The file operations. Production passes <see cref="RealPromoteFileSystem.Instance"/>.</param>
    /// <param name="request">Paths and rotate style; see <see cref="PromoteRequest"/>.</param>
    /// <param name="decide">The allowlist, <c>(fileName, sha256Hex) =&gt; AllowDecision</c>.</param>
    /// <param name="inspect">
    /// Image check. Gets the bytes and the path of the staged temp (already written and closed),
    /// returns "" to accept or the refusal reason. Never called for a refused image.
    /// </param>
    /// <param name="tempName">Temp file name factory; defaults to <see cref="NewTempName"/>.</param>
    /// <param name="verifyPause">
    /// Backoff between verify read attempts (R34 finding 13), given the 1-based attempt number.
    /// Defaults to a short <see cref="Thread.Sleep"/>; tests pass a no-op.
    /// </param>
    public static PromoteResult Promote(
        IPromoteFileSystem fs,
        PromoteRequest request,
        Func<string, string, AllowDecision> decide,
        Func<byte[], string, string> inspect,
        Func<string>? tempName = null,
        Action<int>? verifyPause = null)
    {
        if (fs == null) throw new ArgumentNullException(nameof(fs));
        if (request == null) throw new ArgumentNullException(nameof(request));
        if (decide == null) throw new ArgumentNullException(nameof(decide));
        if (inspect == null) throw new ArgumentNullException(nameof(inspect));

        string current = request.CurrentPath;
        string liveName = Path.GetFileName(current);
        string row = string.IsNullOrEmpty(request.RowName) ? liveName : request.RowName!;

        byte[] bytes;
        try
        {
            bytes = fs.ReadAllBytes(request.NextPath);
        }
        catch (Exception e)
        {
            return new PromoteResult
            {
                Status = PromoteStatus.ReadFailed,
                Error = request.Label + ": could not read staged update: " + e.Message,
            };
        }

        string dir = Path.GetDirectoryName(current) ?? "";
        if (!string.IsNullOrEmpty(dir)) fs.CreateDirectory(dir);

        string staging = string.IsNullOrEmpty(request.StagingDirectory) ? dir : request.StagingDirectory;
        if (!string.IsNullOrEmpty(staging)) fs.CreateDirectory(staging);
        string tmp = Path.Combine(staging, (tempName ?? (() => NewTempName(liveName)))());

        // The gate owns the order: hash once, allowlist decide, and only then inspect. The
        // hash it computed is kept, because it is what the destination is checked against
        // below — that comparison, not the array identity, is what makes the promoted bytes
        // the gated bytes.
        string gatedHash = ReloadAllowPolicy.Sha256Hex(bytes);
        AllowDecision decision;
        string error;
        bool accepted = request.Kind == PromoteKind.Core
            ? CoreReloadPolicy.TryAcceptStagedCoreBytes(
                row, bytes, decide, image => StageAndInspect(fs, tmp, image, inspect), out decision, out error)
            : CoreReloadPolicy.TryAcceptStagedBootstrapBytes(
                row, bytes, decide, image => StageAndInspect(fs, tmp, image, inspect), out decision, out error);

        if (!accepted)
        {
            TryDelete(fs, tmp);
            string? quarantined = null;
            if (!string.IsNullOrEmpty(request.QuarantinePath))
            {
                try
                {
                    if (fs.Exists(request.QuarantinePath!)) fs.Delete(request.QuarantinePath!);
                    fs.Move(request.NextPath, request.QuarantinePath!);
                    quarantined = request.QuarantinePath;
                }
                catch
                {
                    // Quarantine is best-effort; the live image is untouched either way.
                }
            }

            return new PromoteResult
            {
                Status = ReloadAllowPolicy.MayLoad(decision) ? PromoteStatus.Rejected : PromoteStatus.Refused,
                Error = error,
                Decision = decision,
                GatedHash = gatedHash,
                QuarantinedAs = quarantined,
            };
        }

        string backup = string.IsNullOrEmpty(request.BackupPath)
            ? current + CoreImagePolicy.PrevSuffix
            : request.BackupPath;

        // R34 finding 11: the restore must run only when the rotate actually mutated the live
        // path. Rotate's first statement is a cleanup delete of the backup, and on Windows that
        // is the statement most likely to throw (a `.prev` still holding a mapped image is
        // exactly why it survived the last promote). An exception there used to reach the catch
        // below and revert a perfectly good live image to a stale `.prev` — at shutdown, where
        // nothing would notice until the next launch ran the old Bootstrap.
        bool rotatedLive = false;
        try
        {
            Rotate(fs, request, tmp, current, backup, ref rotatedLive);
        }
        catch
        {
            TryDelete(fs, tmp);
            // The move-aside landed and the move-in did not; put the old image back so the folder
            // is never left without a live file. If nothing moved, the live image is untouched
            // and the backup must not be disturbed.
            if (rotatedLive) RestoreFrom(fs, backup, current, out _);
            throw;
        }
        finally
        {
            TryDelete(fs, tmp);
        }

        // R32 finding 4: the rotate moved a FILE, so this is the first moment the promoted
        // bytes can be spoken about. Read them back and compare to the hash the allowlist
        // actually approved. A bare Move with no verify cannot fail this way and that is the
        // point — reverting to one makes the swap test below go green-to-red.
        //
        // R33 finding 10: a throwing read and a hash that differs are *different events* and
        // used to produce the same line. A read that throws compared nothing, so it cannot say
        // anything about a swap; it is almost always the local file system being busy with the
        // file we closed a moment ago. It gets its own status, its own message with
        // `class=io-error`, warning level at the caller, and — this is the part that mattered —
        // it does NOT roll back, so the update lands instead of being retried forever. `.next`
        // survives (the deletes below are past this return), so the next start rotates the same
        // bytes and verifies them again; when the disk is no longer busy the promote completes
        // normally and the warning stops. Only a hash that was actually read and actually
        // differs is `VerifyFailed`, `class=hash-mismatch`, restore, error level.
        //
        // R34 finding 13: "did the read throw" is a signal the adversary owns — a process that
        // holds the live file FileShare.None picks the branch. So the read is retried a bounded
        // number of times in-process, and only a failure this sequence can positively classify as
        // a sharing/lock violation keeps the image. Everything else is a verify that did not
        // happen, which the promote's own contract says is not a promote: restore.
        string promotedHash = "";
        Exception? readError = null;
        for (int attempt = 1; attempt <= VerifyReadAttempts; attempt++)
        {
            try
            {
                promotedHash = ReloadAllowPolicy.Sha256Hex(fs.ReadAllBytes(current));
                readError = null;
                break;
            }
            catch (Exception e)
            {
                readError = e;
                if (!IsTransientSharingFailure(e) || attempt == VerifyReadAttempts) break;
                (verifyPause ?? DefaultVerifyPause)(attempt);
            }
        }

        if (readError != null && IsTransientSharingFailure(readError))
        {
            return new PromoteResult
            {
                Status = PromoteStatus.VerifyUnreadable,
                Decision = decision,
                GatedHash = gatedHash,
                PromotedHash = "",
                Restored = false,
                Error = FormatVerifyUnreadable(request.Label, liveName, readError.Message),
            };
        }

        if (readError != null)
        {
            bool restoredAfterRead = RestoreFrom(fs, backup, current, out string readNote);
            return new PromoteResult
            {
                Status = PromoteStatus.VerifyFailed,
                Decision = decision,
                GatedHash = gatedHash,
                PromotedHash = "",
                Restored = restoredAfterRead,
                Error = Append(
                    restoredAfterRead
                        ? FormatVerifyUnverifiable(request.Label, liveName, readError.Message)
                        : FormatRestoreFailed(request.Label, liveName, Path.GetFileName(backup), readError.Message),
                    readNote),
            };
        }

        if (!string.Equals(promotedHash, gatedHash, StringComparison.OrdinalIgnoreCase))
        {
            bool restored = RestoreFrom(fs, backup, current, out string note);
            return new PromoteResult
            {
                Status = PromoteStatus.VerifyFailed,
                Decision = decision,
                GatedHash = gatedHash,
                PromotedHash = promotedHash,
                Restored = restored,
                Error = Append(
                    restored
                        ? FormatVerifyFailed(request.Label, liveName, gatedHash, promotedHash)
                        : FormatRestoreFailed(request.Label, liveName, Path.GetFileName(backup), "backup missing or unmovable"),
                    note),
            };
        }

        try { fs.Delete(request.NextPath); } catch { /* deploy may still have it open */ }
        if (request.DeleteBackupAfterPromote)
        {
            try { if (fs.Exists(backup)) fs.Delete(backup); } catch { /* still mapped by this process */ }
        }

        return new PromoteResult
        {
            Status = PromoteStatus.Promoted,
            Decision = decision,
            GatedHash = gatedHash,
            PromotedHash = promotedHash,
        };
    }

    /// <summary>
    /// Write the gated bytes to the exclusive temp ourselves, close it, and only then let the
    /// caller's check look at it. The temp exists so a metadata read
    /// (<c>AssemblyName.GetAssemblyName</c>) has a file to open; it is written from
    /// <paramref name="image"/>, never re-read from <c>.next</c>.
    /// </summary>
    private static string StageAndInspect(
        IPromoteFileSystem fs, string tmp, byte[] image, Func<byte[], string, string> inspect)
    {
        try
        {
            fs.WriteNewExclusive(tmp, image);
        }
        catch (Exception e)
        {
            // A name we just generated that already exists, or one we cannot hold exclusively,
            // is a refusal — never a fall back to a shared temp.
            return "could not stage the image for inspection: " + e.Message;
        }

        return inspect(image, tmp) ?? "";
    }

    /// <summary>
    /// Move the staged temp onto the live path. <paramref name="movedCurrent"/> is set the moment
    /// the live path has been mutated and is <em>never</em> cleared, so a caller that catches an
    /// exception can tell "nothing happened" from "half of it happened" (R34 finding 11). It is a
    /// <c>ref</c> rather than an <c>out</c> for exactly that reason: an <c>out</c> is not assigned
    /// on the exception path.
    /// </summary>
    private static void Rotate(
        IPromoteFileSystem fs, PromoteRequest request, string tmp, string current, string backup,
        ref bool movedCurrent)
    {
        if (!fs.Exists(current))
        {
            fs.Move(tmp, current);
            movedCurrent = true;
            return;
        }

        if (request.UseReplace)
        {
            // Cleanup, not rotation: if this throws, the live path still holds the old image.
            if (fs.Exists(backup)
                && !string.Equals(backup, current + CoreImagePolicy.PrevSuffix, StringComparison.OrdinalIgnoreCase))
                fs.Delete(backup);
            fs.Replace(tmp, current, backup);
            movedCurrent = true;
            return;
        }

        // Same here: a `.prev` that will not delete (Windows refuses while an image section is
        // live) leaves the live image exactly where it was, so no restore is owed.
        if (fs.Exists(backup)) fs.Delete(backup);
        fs.Move(current, backup);
        movedCurrent = true;
        fs.Move(tmp, current);
    }

    /// <summary>
    /// Suffix every parked rejected image carries, after its random component. Never a whole path
    /// on its own — see <see cref="NewRejectedName"/>.
    /// </summary>
    public const string RejectedSuffix = ".rejected.tmp";

    /// <summary>
    /// A park name for one restore: the live file's name, a fresh GUID, and
    /// <see cref="RejectedSuffix"/>. Random for the same reason <see cref="NewTempName"/> is
    /// (R34 finding 5) — the old constant <c>&lt;live&gt;.rejected.tmp</c> was a published name
    /// an attacker could hold, and holding it disabled the rollback outright.
    /// </summary>
    public static string NewRejectedName(string liveFileName) =>
        (string.IsNullOrEmpty(liveFileName) ? "rejected" : liveFileName)
        + "." + Guid.NewGuid().ToString("N") + RejectedSuffix;

    /// <summary>
    /// Put <paramref name="backup"/> back at <paramref name="current"/>. True when the live path
    /// ends up holding it.
    ///
    /// <para><b>R33 finding 9.</b> This used to <c>Delete(current)</c> and then
    /// <c>Move(backup, current)</c>. Between those two calls the live path held nothing, and the
    /// move is exactly the operation most likely to fail here — an anti-virus scan, an indexer or
    /// a second process mid-promote holding either file (the same class of holder that makes the
    /// backup delete at the end of <see cref="Promote"/> best-effort). The rollback that exists so
    /// the folder is never left without a live file could therefore leave it without one, and for
    /// Bootstrap that means BepInEx has nothing to chainload and Progenitor is gone until somebody
    /// restores it by hand. The refusal path must be the safe one.</para>
    ///
    /// <para>So: <c>File.Replace(backup, current, rejected)</c> first — one atomic operation in
    /// which <paramref name="current"/> is never absent, and which parks the unverified image at
    /// <c><paramref name="current"/> + <see cref="RejectedSuffix"/></c> rather than destroying it.
    /// If <c>Replace</c> is unavailable or refuses (it needs both paths on one volume, and not
    /// every file system implements it), the live file is still <em>there</em> — nothing has been
    /// deleted yet — and the fallback moves it aside, moves the backup in, and, if that second
    /// move fails, moves the aside copy back so an image exists either way. That last case
    /// returns false, so the caller still logs <see cref="FormatRestoreFailed"/>, whose wording
    /// ("the live file may be an image the allowlist never approved") is then literally true.
    /// A live image nobody approved is bad; no live image at all is worse, and the operator is
    /// told which one they have.</para>
    ///
    /// <para><b>R34 finding 5.</b> Two things about the park. First, the name: it was
    /// <c><paramref name="current"/> + RejectedSuffix</c>, a compile-time constant in the
    /// attacker-writable folder — the exact mistake R32 finding 4 made with the promote temp. A
    /// process holding a zero-byte file there made <c>Replace</c> throw, made the fallback's
    /// "do not stack" check bail, and so returned false with the unverified image still live —
    /// and did it for every restore thereafter. The name is now
    /// <see cref="NewRejectedName"/>, random per restore.</para>
    ///
    /// <para>Second, and more important than the name: <b>the park is evidence, not a
    /// precondition.</b> If the aside move cannot run, the rejected image is deleted and the
    /// backup moved in regardless, with <see cref="FormatParkFailed"/> appended to the error line
    /// the caller logs. Losing the evidence copy is a cost; leaving a detected swap live because
    /// we could not save a souvenir of it is not a trade this sequence is allowed to make. Note
    /// this narrows R33 finding 9's availability preference to the case it was actually about — a
    /// rotate that half-happened — rather than the case where we have positively detected that
    /// the live image is not the approved one.</para>
    /// </summary>
    /// <param name="parkNote">
    /// Empty normally; <see cref="FormatParkFailed"/> when the rejected image had to be deleted
    /// instead of parked. The caller appends it to the error line, which is logged at error.
    /// </param>
    private static bool RestoreFrom(IPromoteFileSystem fs, string backup, string current, out string parkNote)
    {
        parkNote = "";
        try
        {
            if (!fs.Exists(backup)) return false;

            // The rotate's move-aside succeeded and its move-in did not: nothing to preserve,
            // and a plain move can never leave the path emptier than it already is.
            if (!fs.Exists(current))
            {
                fs.Move(backup, current);
                return true;
            }

            string dir = Path.GetDirectoryName(current) ?? "";
            string parkName = NewRejectedName(Path.GetFileName(current));
            string rejected = string.IsNullOrEmpty(dir) ? parkName : Path.Combine(dir, parkName);

            try
            {
                // Atomic: current goes from the unverified image to the backup with no moment
                // in between where it is missing.
                fs.Replace(backup, current, rejected);
                TryDelete(fs, rejected);
                return true;
            }
            catch
            {
                // Fall through to the move pair. Nothing has been deleted, so the live path
                // still holds the unverified image at this point.
            }

            bool parked = false;
            try
            {
                if (!fs.Exists(rejected))
                {
                    fs.Move(current, rejected);
                    parked = true;
                }
            }
            catch
            {
                // Cannot park. Handled below — never by abandoning the restore.
            }

            if (!parked)
            {
                parkNote = FormatParkFailed(Path.GetFileName(current));
                try
                {
                    fs.Delete(current);
                }
                catch
                {
                    // The unverified image can be neither moved aside nor deleted, so it is still
                    // the live file. That is the honest `Restored = false` the caller reports.
                    return false;
                }
            }

            try
            {
                fs.Move(backup, current);
            }
            catch
            {
                // The backup would not move. Put the unverified image back rather than leaving
                // the folder with no live file at all — when we still have a copy of it.
                try { if (parked && !fs.Exists(current)) fs.Move(rejected, current); } catch { /* nothing left to try */ }
                return false;
            }

            if (parked) TryDelete(fs, rejected);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static void TryDelete(IPromoteFileSystem fs, string path)
    {
        try { if (fs.Exists(path)) fs.Delete(path); } catch { /* leftover temp */ }
    }
}
