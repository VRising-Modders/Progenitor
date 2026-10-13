using System;
using System.IO;
using System.Reflection;
using BepInEx;

namespace Progenitor.Bootstrap;

/// <summary>
/// Bootstrap is mapped by the chainloader, so a newer copy is staged as
/// <c>Progenitor.Bootstrap.dll.next</c>. Promotes at process exit and at the next startup.
/// <c>Application.quitting</c> uses <c>DelegateSupport.ConvertDelegate</c>, which caches an
/// IL2CPP delegate — that is why this subscription lives here and not in Core.
///
/// Step C: Core lives under <c>BepInEx/progenitor-core/</c> and is not yet mapped when Bootstrap
/// <c>Load</c>s, so a staged <c>Progenitor.dll.next</c> is promoted <em>before</em>
/// <c>Assembly.LoadFrom</c> and takes effect this launch. Loop scripts still promote both
/// before spawn because a forced kill never runs these handlers.
/// </summary>
internal static class SelfUpdate
{
    private const string NextSuffix = ".next";
    private const string PrevSuffix = ".prev";

    public static void Install()
    {
        if (PromoteCore(out var coreMsg))
            ProcessHost.Log?.LogInfo(coreMsg);
        if (PromoteBootstrap(out var msg))
            ProcessHost.Log?.LogWarning($"{msg} It takes effect on the next launch; this process is still running the previous Bootstrap.");
        AppDomain.CurrentDomain.ProcessExit += (_, _) => PromoteAtExit();
        try
        {
            // ConvertDelegate caches an IL2CPP delegate for process lifetime. This handler
            // lives in Bootstrap (default ALC) and also raises Core's LifetimeQuitting slot
            // so collectible Core never ConvertDelegates Application.quitting.
            UnityEngine.Application.quitting += Il2CppInterop.Runtime.DelegateSupport.ConvertDelegate<Il2CppSystem.Action>((Action)OnQuitting);
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"Bootstrap: could not subscribe to Application.quitting: {e.Message}. Staged updates apply at the next startup instead.");
        }
    }

    private static bool _exitHandled;

    private static void OnQuitting()
    {
        ProcessHost.InvokeLifetimeQuitting();
        // AUTOUPDATE5: a clean quit of a process whose Core installed is not a failed start of a release on trial.
        BootIdentity.NoteCleanExit();
        PromoteAtExit();
    }

    private static void PromoteAtExit()
    {
        if (_exitHandled) return;
        _exitHandled = true;
        SweepPluginsAtExit();
        if (PromoteCore(out var coreMsg)) ProcessHost.Log?.LogInfo(coreMsg);
        if (PromoteBootstrap(out var m)) ProcessHost.Log?.LogInfo(m);
    }

    /// <summary>
    /// R31 finding 18: <c>Shutdown</c> was a live signal nothing read. Bootstrap observed
    /// <c>ProcessExit</c> (and <c>Application.quitting</c>) and promoted files, but nothing
    /// unloaded reload-folder plugins at exit, so no <c>OnUnload</c> ever saw the reason
    /// <c>docs/modules.md</c> lists as one of the four shapes an operator sees. Whoever observes
    /// the exit is now the one that drives the unload: the slot is set first, so a plugin torn
    /// down anywhere on this stack reads <c>reason=Shutdown</c>, and
    /// <see cref="ReloaderHost.UnloadAllForShutdown"/> runs the sweep in dependency order before
    /// the promotes below touch any file.
    ///
    /// Still best-effort by nature: a forced kill (<c>taskkill</c>, <c>test-server.ps1 stop</c>)
    /// raises neither event, and nothing here may throw into the runtime's exit path.
    /// </summary>
    private static void SweepPluginsAtExit()
    {
        try
        {
            PluginUnloadSlot.NoteProcessExiting();
            int n = ReloaderHost.UnloadAllForShutdown();
            if (n > 0)
                ProcessHost.Log?.LogInfo(
                    $"Reload: unloaded {n} plugin file(s) at process exit "
                    + $"(unload reason={PluginUnloadSlot.Name(PluginUnloadSlot.Shutdown)}).");
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning($"Reload: process-exit unload failed: {e.Message}");
        }
    }

    private static bool PromoteBootstrap(out string message)
    {
        message = "";
        try
        {
            var current = Assembly.GetExecutingAssembly().Location;
            if (string.IsNullOrEmpty(current)) return false;
            return PromoteFile(current, "Bootstrap", out message);
        }
        catch (Exception e)
        {
            message = $"Bootstrap: could not promote staged update: {e.Message}";
            ProcessHost.Log?.LogWarning(message);
            return false;
        }
    }

    /// <summary>
    /// Promote a staged <c>Progenitor.dll.next</c> now so <see cref="CoreLoader.RequestReload"/>
    /// loads the copy deploy just dropped. No-op when no <c>.next</c> exists.
    /// Notifies <see cref="CoreWatch"/> so the watch stamp advances only on a
    /// successful rotate (a failed <c>ReadAllBytes</c> retries the same file).
    /// </summary>
    internal static bool TryPromoteCoreNow() => TryPromoteCoreNow(requireMatchingProtocol: true);

    /// <summary>
    /// Same as <see cref="TryPromoteCoreNow()"/> with an explicit protocol gate.
    /// First load / Core-less recovery pass <paramref name="requireMatchingProtocol"/>
    /// false so a throwaway-ALC protocol read of 0 cannot quarantine a matching
    /// image before <c>LoadFromStream</c>.
    /// </summary>
    internal static bool TryPromoteCoreNow(bool requireMatchingProtocol)
    {
        string next;
        try
        {
            next = CoreWatchPolicy.NextPath(CoreLayout.DllPath(Paths.BepInExRootPath));
        }
        catch
        {
            next = "";
        }

        bool hadNext = !string.IsNullOrEmpty(next) && File.Exists(next);
        bool ok;
        string msg;
        try
        {
            ok = PromoteCore(out msg, requireMatchingProtocol);
        }
        catch (Exception e)
        {
            msg = "Core: could not promote staged update: " + e.Message;
            ProcessHost.Log?.LogWarning(msg);
            ok = false;
        }

        if (ok && !string.IsNullOrEmpty(msg))
            ProcessHost.Log?.LogInfo(msg);
        if (hadNext)
        {
            if (ok) CoreWatch.NotePromoteSucceeded();
            else CoreWatch.NotePromoteFailed(msg);
        }
        return ok;
    }

    private static bool PromoteCore(out string message) =>
        PromoteCore(out message, requireMatchingProtocol: false);

    private static bool PromoteCore(out string message, bool requireMatchingProtocol)
    {
        message = "";
        try
        {
            var root = Paths.BepInExRootPath;
            if (string.IsNullOrEmpty(root)) return false;
            var current = CoreLayout.DllPath(root);
            return PromoteFile(current, "Core", out message, requireMatchingProtocol);
        }
        catch (Exception e)
        {
            message = $"Core: could not promote staged update: {e.Message}";
            ProcessHost.Log?.LogWarning(message);
            return false;
        }
    }

    private static bool PromoteFile(string current, string label, out string message) =>
        PromoteFile(current, label, out message, requireMatchingProtocol: true);

    private static bool PromoteFile(string current, string label, out string message, bool requireMatchingProtocol)
    {
        message = "";
        var next = current + NextSuffix;
        if (!File.Exists(next)) return false;

        if (string.Equals(label, "Core", StringComparison.Ordinal))
            return PromoteCoreFile(current, next, out message, requireMatchingProtocol);

        return PromoteBootstrapFile(current, next, label, out message);
    }

    /// <summary>
    /// The sixth automatic code-loading path, gated (R31 finding 1) and write-verified (R32
    /// finding 4). Read <c>Progenitor.Bootstrap.dll.next</c> into memory <em>once</em>, ask the
    /// allowlist about those exact bytes, and only then check PE sanity: a complete managed PE
    /// whose assembly name is <c>Progenitor.Bootstrap</c>. No <c>LoadFromStream</c> and no
    /// reflection over the image - Bootstrap is not collectible and this promote does not map
    /// it, so the name is read from the staged file's metadata
    /// (<see cref="AssemblyName.GetAssemblyName"/>, which reads metadata and runs no code) off a
    /// temp <see cref="PromoteSequence"/> wrote from the hashed bytes under a random name it
    /// holds exclusively.
    ///
    /// The sequence itself lives in <see cref="PromoteSequence.Promote"/> behind
    /// <see cref="IPromoteFileSystem"/>. R32 finding 4: the old code here hashed an array and
    /// then <c>File.Move</c>d a file from a constant, attacker-writable path, so the gated bytes
    /// and the promoted bytes were different objects. The destination is now re-read after the
    /// rotate and compared to the gated hash, with a restore from <c>.prev</c> and an error line
    /// on mismatch - and a test drives that exact swap through the seam.
    ///
    /// A refused or malformed image is quarantined as <c>*.failed-*</c> and the live Bootstrap
    /// is left exactly as it was; nothing about the running process changes. This runs on every
    /// process start (<see cref="Install"/>) and again at quit
    /// (<see cref="PromoteAtExit"/>), which are the two moments the old code moved unexamined
    /// bytes over the assembly that enforces the allowlist for every other path.
    /// </summary>
    private static bool PromoteBootstrapFile(string current, string next, string label, out string message)
    {
        message = "";
        string? dir = Path.GetDirectoryName(current);
        string failed = CoreReloadPolicy.StagedQuarantineName(Path.GetFileName(current), DateTime.UtcNow);

        var request = new PromoteRequest(current, next, label, PromoteKind.Bootstrap)
        {
            RowName = Path.GetFileName(current),
            BackupPath = current + PrevSuffix,
            DeleteBackupAfterPromote = true,
            QuarantinePath = string.IsNullOrEmpty(dir) ? failed : Path.Combine(dir, failed),
        };

        var result = PromoteSequence.Promote(
            RealPromoteFileSystem.Instance,
            request,
            ReloadAllowStore.Decide,
            InspectStagedBootstrap);

        switch (result.Status)
        {
            case PromoteStatus.Promoted:
                message = $"{label}: promoted {Path.GetFileName(next)} to {Path.GetFileName(current)}.";
                return true;

            case PromoteStatus.ReadFailed:
                message = result.Error;
                ProcessHost.Log?.LogWarning(message);
                return false;

            case PromoteStatus.VerifyFailed:
                // The one line an operator must never miss: something replaced the image
                // between the gate and the rotate. class=hash-mismatch.
                ProcessHost.Log?.LogError(result.Error);
                return false;

            case PromoteStatus.VerifyUnreadable:
                // R33 finding 10: the re-read threw, so nothing was compared. A warning, not the
                // attack line, and not a rollback: the promoted image stays and .next stays, so
                // this start's verify is retried on the next one. Bootstrap promotes once per
                // start, so a transient holder costs exactly one warning line.
                //
                // R34 finding 13: reaching this case is no longer the attacker's choice. The
                // sequence retries the read (PromoteSequence.VerifyReadAttempts) and only a
                // sharing or lock violation lands here; every other read failure comes back as
                // VerifyFailed and is logged at error by the case above.
                message = result.Error;
                ProcessHost.Log?.LogWarning(message);
                return false;

            default:
                if (result.Status == PromoteStatus.Refused)
                    ProcessHost.Log?.LogError(result.Error);
                else
                    ProcessHost.Log?.LogError(CoreReloadPolicy.FormatRejectedBootstrapNext(result.Error));
                if (result.QuarantinedAs != null)
                    ProcessHost.Log?.LogWarning(
                        CoreReloadPolicy.FormatQuarantinedBootstrapNext(Path.GetFileName(result.QuarantinedAs)));
                else
                    ProcessHost.Log?.LogWarning(
                        $"Bootstrap: quarantine .next failed (live Bootstrap untouched).");
                return false;
        }
    }

    /// <summary>
    /// PE + assembly-name check for the staged Bootstrap bytes. The temp was written from those
    /// same bytes by <see cref="PromoteSequence"/> (so the name comes from the hashed image, not
    /// from a second read of <c>.next</c>); this reads the metadata name without loading the
    /// assembly.
    /// </summary>
    private static string InspectStagedBootstrap(byte[] image, string tmp)
    {
        string? name = null;
        try
        {
            name = AssemblyName.GetAssemblyName(tmp).Name;
        }
        catch (Exception e)
        {
            // Anything unreadable here is a refusal, never a pass: the static PE check below
            // still runs and reports the shape, and a missing name is itself a refusal.
            ProcessHost.Log?.LogWarning(
                $"Bootstrap: could not read the staged assembly name: {e.Message}");
        }
        return CoreReloadPolicy.CheckStagedBootstrapImage(image, name);
    }

    /// <summary>
    /// Read <c>.next</c> into memory <em>once</em>, ask the allowlist about those exact bytes,
    /// and only then validate PE + Runtime type + Install + protocol (which loads the image
    /// into a throwaway ALC). An unlisted or malformed verdict quarantines <c>.next</c>, logs
    /// <see cref="ReloadAllowPolicy.FormatRefusedCore"/> and leaves the live Core running:
    /// this is the fifth automatic code-loading path and R30 finding 1 is that it was the one
    /// path the gate did not cover. The same bytes are written over the live dll below, so
    /// there is no re-read between the hash and the promote. Then write the bytes to a
    /// same-folder temp and <c>File.Replace</c> over the
    /// live path. Backup is <c>.prev</c> when that file is absent (first proven
    /// rotate); an existing <c>.prev</c> is the last proven image and is left
    /// alone, with the unproven live file quarantined as <c>failed-*</c>.
    /// Invalid <c>.next</c> is quarantined without touching the live dll.
    /// <c>.prev</c> is never deleted here — only after Install proves N+1.
    /// </summary>
    private static bool PromoteCoreFile(string current, string next, out string message) =>
        PromoteCoreFile(current, next, out message, requireMatchingProtocol: true);

    private static bool PromoteCoreFile(string current, string next, out string message, bool requireMatchingProtocol)
    {
        message = "";

        // R32 finding 4, Core half: this used to WriteAllBytes the gated array to the constant
        // `Progenitor.dll.promote.tmp` and File.Replace that FILE into place, so what landed was
        // whatever the file held at rotate time rather than what was hashed. Same sequence, same
        // seam, same write-verify as Bootstrap now -- the only differences are the gate
        // (TryAcceptStagedCoreBytes, which loads into a throwaway ALC) and the rotate
        // (File.Replace, keeping .prev until Install proves N+1).
        var prev = CoreImagePolicy.PrevPath(current);
        var request = new PromoteRequest(current, next, "Core", PromoteKind.Core)
        {
            RowName = CoreLayout.FileName,
            BackupPath = CoreImagePolicy.ShouldUsePrevAsReplaceBackup(File.Exists(prev))
                ? prev
                : CoreWatchPolicy.QuarantinePath(current, DateTime.UtcNow),
            UseReplace = true,
            DeleteBackupAfterPromote = false,
            // Core's quarantine is CoreLoader's, which also writes the last-good copy; the
            // sequence must not move .next out from under it.
            QuarantinePath = null,
        };

        var result = PromoteSequence.Promote(
            RealPromoteFileSystem.Instance,
            request,
            ReloadAllowStore.Decide,
            (image, _) => TryAcceptStagedCore(image, requireMatchingProtocol, out var why) ? "" : why);

        switch (result.Status)
        {
            case PromoteStatus.Promoted:
                ProcessHost.Log?.LogInfo(CoreImagePolicy.FormatKeptPrev());
                message = CoreReloadPolicy.FormatPromotedNext();
                return true;

            case PromoteStatus.ReadFailed:
                message = result.Error;
                ProcessHost.Log?.LogWarning(message);
                return false;

            case PromoteStatus.VerifyFailed:
                ProcessHost.Log?.LogError(result.Error);
                message = result.Error;
                return false;

            case PromoteStatus.VerifyUnreadable:
                // Busy disk, not a swap (R33 finding 10), and — since R34 finding 13 — a busy
                // disk the sequence identified rather than assumed: a bounded in-process retry
                // ran first, and only a sharing or lock violation classifies here. The rotated
                // image is live and .next is untouched; the Core watch's own bounded retry
                // re-verifies it, and a first load or a bridge core_reload does the same on the
                // next attempt. The retry also stops a one-second scanner hold from burning the
                // watch's five-failure budget on an update that is already live.
                ProcessHost.Log?.LogWarning(result.Error);
                message = result.Error;
                return false;

            default:
                if (!ReloadAllowPolicy.MayLoad(result.Decision))
                    ProcessHost.Log?.LogError(result.Error);
                CoreLoader.QuarantineNextFile(next, result.Error);
                message = CoreImagePolicy.FormatRejectedNext(result.Error);
                return false;
        }
    }

    /// <summary>
    /// First load still requires a complete Core PE with Runtime.Install.
    /// Protocol is checked at <c>LoadAndInstall</c> so a throwaway inspect ALC
    /// that reads protocol 0 cannot quarantine the staged image.
    /// </summary>
    private static bool TryAcceptStagedCore(byte[] bytes, bool requireMatchingProtocol, out string error) =>
        CoreLoader.TryValidateCoreImage(bytes, requireMatchingProtocol, out error);
}
