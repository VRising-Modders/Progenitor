using System;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;

namespace Progenitor.Bootstrap;

/// <summary>
/// AUTOUPDATE5: the running Bootstrap's identity (its file, sha256 and version), captured at the start of <c>Load</c>
/// before any promote can rename the file, and the start-loop guard (<see cref="Progenitor.SelfUpdate.BootHealth"/>) run
/// right after. Core and the Dev bridge read these by reflection (Core does not reference Bootstrap): the bridge's
/// <c>ping</c> lists the image as <c>Bootstrap</c>, Core's self-update compares it with a release.
/// </summary>
public static class BootIdentity
{
    /// <summary>The sha256 of the Bootstrap image this process runs (lower-case hex), or empty when it could not be read.</summary>
    public static string Sha256 { get; private set; } = "";

    /// <summary>The running Bootstrap's file.</summary>
    public static string Path { get; private set; } = "";

    public static string Version => MyPluginInfo.PLUGIN_VERSION;

    /// <summary>What the start-loop guard did at this start.</summary>
    public static string BootHealthResult => Progenitor.SelfUpdate.BootHealth.LastResult;

    internal static void Capture()
    {
        try
        {
            Path = Assembly.GetExecutingAssembly().Location ?? "";
            if (Path.Length == 0 || !File.Exists(Path)) return;
            using var sha = SHA256.Create();
            using var s = new FileStream(Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var h = sha.ComputeHash(s);
            var sb = new StringBuilder(h.Length * 2);
            foreach (byte b in h) sb.Append(b.ToString("x2"));
            Sha256 = sb.ToString();
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogWarning("Bootstrap: could not hash its own image: " + e.Message);
        }
    }

    /// <summary>
    /// A clean quit (<c>Application.quitting</c>) of a process whose Core installed (it attached to Bootstrap): the start the
    /// guard counted is taken back. A Core that failed to install never attaches, so its start stays counted. Never throws.
    /// </summary>
    internal static void NoteCleanExit()
    {
        try
        {
            var image = CoreLoader.LoadedImage;
            if (!ProcessHost.CoreAttached || image == null || image.Length == 0) return;
            string result = Progenitor.SelfUpdate.BootHealth.NoteCleanExit(BepInEx.Paths.BepInExRootPath, Sha256, Progenitor.SelfUpdate.SelfFiles.Sha256(image));
            if (result.StartsWith("clean exit", StringComparison.Ordinal)) ProcessHost.Log?.LogInfo("AutoUpdate: progenitor " + result);
        }
        catch { /* never into the quit path */ }
    }

    /// <summary>Capture the identity, then run the start-loop guard (before Core is read). Never throws.</summary>
    internal static void CaptureAndCheck(string bepInExRoot, BepInEx.Logging.ManualLogSource log)
    {
        Capture();
        try
        {
            Progenitor.SelfUpdate.BootHealth.BootstrapPathHint = Path.Length > 0 ? Path : null;
            Progenitor.SelfUpdate.BootHealth.RunAtStart(bepInExRoot, Path, line => log.LogInfo(line), line => log.LogError(line));
        }
        catch (Exception e)
        {
            log.LogWarning("AutoUpdate: progenitor start check threw: " + e.Message);
        }
    }
}
