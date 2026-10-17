using System;
using System.Diagnostics;
using System.IO;
using BepInEx;
using BepInEx.Preloader.Core.Patching;
using Progenitor.PluginActions;

namespace Progenitor.Preloader;

/// <summary>
/// SETTINGS1 stage 3: this patcher's main job is to carry out the Mods menu's next-start
/// requests for ordinary BepInEx plugins (<see cref="PluginFileActions"/>). BepInEx runs a
/// patcher's <see cref="Initialize"/> before it patches anything and before the chainloader has
/// opened a single plugin file, so a DLL can be renamed or its folder moved here without fighting a
/// lock. It declares no <c>[TargetAssembly]</c> method, so it patches nothing.
///
/// <para>It references no Core and no game type. Everything it does is in
/// <c>PluginFileActions.cs</c>, which the tests run as-is: it only ever renames or moves files
/// strictly inside <c>BepInEx/plugins</c>, never a <c>Progenitor*</c> or <c>BepInEx*</c> file,
/// never through a link, and never a file whose bytes changed since the request. Every outcome is
/// written back to <c>BepInEx/config/progenitor-plugin-actions.json</c>, where the Mods menu
/// reconciles it. Anything unexpected is logged and the game starts normally.</para>
///
/// <para>SUPPORT1: it also keeps a copy of each session's log under <c>BepInEx/progenitor-logs</c>
/// (<see cref="SessionLogListener"/>), so the Get help bundle still has a crashed session's log after a restart.</para>
///
/// <para>Review finding 2: while another game process started from this same install is running
/// (it has the plugin DLLs open), the requests wait for a start with no such process.</para>
/// </summary>
[PatcherPluginInfo(Guid, "Progenitor Preloader", "1.0.0")]
public sealed class Patcher : BasePatcher
{
    /// <summary>The patcher's GUID. Never a chainloader plugin GUID.</summary>
    public const string Guid = "progenitor.preloader";

    private static readonly string[] GameExecutables = { "VRising", "VRisingServer" };

    /// <inheritdoc />
    public override void Initialize()
    {
        // Single-folder install: Bootstrap placed this copy; with Progenitor gone it does nothing and steps aside.
        try
        {
            string bep = Paths.BepInExRootPath;
            if (!string.IsNullOrEmpty(bep) && !ProgenitorPresence.BootstrapInstalled(bep))
            {
                string own = "";
                try { own = typeof(Patcher).Assembly.Location ?? ""; }
                catch { own = ""; }
                Log.LogInfo(ProgenitorPresence.Retire(bep, own));
                return;
            }
        }
        catch { /* the game must start */ }

        // SUPPORT1: this session's copy of the log first, so a crashed session survives the next start.
        try { Log.LogInfo(SessionLogListener.Attach()); }
        catch { /* the game must start */ }

        try
        {
            string root = Paths.BepInExRootPath;
            PluginFileActions.RunAll(
                root,
                DateTime.UtcNow,
                line => Log.LogInfo(line),
                line => Log.LogWarning(line),
                OtherGameProcessRunning);
        }
        catch (Exception e)
        {
            try { Log.LogWarning("Progenitor preloader: " + e.Message); }
            catch { /* the game must start */ }
        }
    }

    /// <summary>
    /// True when another game process of this install runs; the rule is
    /// <see cref="PluginFileActions.IsGameProcessOfInstall"/> (a VRising/VRisingServer process whose
    /// path cannot be read counts as running).
    /// </summary>
    private static bool OtherGameProcessRunning()
    {
        string gameRoot = Paths.GameRootPath ?? "";
        int self = Environment.ProcessId;
        foreach (var name in GameExecutables)
        {
            Process[] processes;
            try { processes = Process.GetProcessesByName(name); }
            catch { continue; }

            foreach (var process in processes)
            {
                try
                {
                    int pid;
                    try { pid = process.Id; }
                    catch { continue; }

                    string? file;
                    try { file = process.MainModule?.FileName; }
                    catch { file = null; } // access denied, or it is exiting

                    if (PluginFileActions.IsGameProcessOfInstall(name, file, gameRoot, self, pid)) return true;
                }
                finally
                {
                    process.Dispose();
                }
            }
        }

        return false;
    }
}
