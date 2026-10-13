#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Config;

/// <summary>
/// The one way Core reaches the shared, process-lifetime <c>progenitor.cfg</c>.
///
/// <para>Core installs from inside Bootstrap's <c>Plugin.Load</c>. At that point BepInEx has
/// listed Bootstrap in <c>IL2CPPChainloader.Plugins</c> but has not stored its instance yet, so
/// the old per-module <c>Plugins.TryGetValue(guid).Instance.Config</c> lookup found nothing at
/// process start and every section bound that way was skipped silently until a Core hot reload
/// (test server, 2026-09-24: no <c>Compat: RewriteMode=</c> line at startup). Sources, in order:
/// Bootstrap's handover (<c>ProcessHost.SharedConfig</c>), then that chainloader lookup, which
/// works once the chainloader has finished.</para>
///
/// <para>A section that must bind at install goes through <see cref="BindWhenAvailable"/>: it
/// binds now, or, with a Bootstrap that predates the handover, logs one line and binds once the
/// file resolves (a one-second poll for about a minute). A watched reload of the shared file also
/// retries, but only fires when some other bind already made ConfigHelper watch that path; when
/// every shared bind is deferred nothing watches it. Every bind still goes
/// through <see cref="ConfigHelper"/>, whose type gate keeps Core types off the file.</para>
/// </summary>
internal static class SharedConfig
{
    /// <summary>Polls before the queued sections are reported as unbound (about a minute).</summary>
    internal const int RetryPolls = 60;
    internal const double RetrySeconds = 1.0;

    private static readonly DeferredBinds Pending = new(TryGet, line => Plugin.Logger?.LogWarning(line));
    private static int _generation;
    private static bool _polling;
    private static bool _subscribed;
    private static readonly HashSet<string> ReportedMissing = new(StringComparer.Ordinal);

    /// <summary>The shared file, or null when neither source has it yet. Never throws.</summary>
    internal static ConfigFile? TryGet()
    {
        var handed = BootstrapLink.SharedConfig;
        if (handed != null) return handed;
        try
        {
            var chain = IL2CPPChainloader.Instance;
            if (chain?.Plugins != null
                && chain.Plugins.TryGetValue(MyPluginInfo.PLUGIN_GUID, out var info)
                && info?.Instance is BasePlugin plugin)
                return plugin.Config;
        }
        catch
        {
            // Chainloader not up (tests, a Core loaded some other way): "not available".
        }
        return null;
    }

    /// <summary>
    /// <see cref="TryGet"/> for a caller that reads a setting on demand (Install tab, Mods menu)
    /// and will simply ask again. Logs one line per section per generation while the file is missing.
    /// </summary>
    internal static ConfigFile? TryGetOrLog(string section)
    {
        var file = TryGet();
        if (file != null) return file;
        bool first;
        lock (ReportedMissing) first = ReportedMissing.Add(section ?? "");
        if (first) Plugin.Logger?.LogWarning(DeferredBinds.DeferredLog(section ?? ""));
        return null;
    }

    /// <summary>
    /// Run <paramref name="bind"/> on the shared file now, or queue it until the file resolves.
    /// Returns true when it ran now; on false the caller keeps its defaults, and
    /// <paramref name="bind"/> runs later on the main thread. <paramref name="section"/> names
    /// the section in the log (<c>[Commands]</c>) and de-duplicates a repeated request.
    /// </summary>
    internal static bool BindWhenAvailable(string section, Action<ConfigFile> bind)
    {
        if (Pending.Request(section, bind)) return true;
        EnsureReloadHook();
        StartPolling();
        return false;
    }

    /// <summary>Drop queued binds and the reload hook so no Core delegate outlives the generation.</summary>
    internal static void Uninstall()
    {
        _generation++;
        _polling = false;
        lock (ReportedMissing) ReportedMissing.Clear();
        Pending.Clear();
        if (_subscribed)
        {
            ConfigHelper.Reloaded -= OnReloaded;
            _subscribed = false;
        }
    }

    private static void EnsureReloadHook()
    {
        if (_subscribed) return;
        ConfigHelper.Reloaded += OnReloaded;
        _subscribed = true;
    }

    private static void OnReloaded(ConfigFile file)
    {
        if (file == null || !ConfigHelper.IsSharedConfigFile(file)) return;
        Pending.Retry("shared config reload", file);
    }

    private static void StartPolling()
    {
        if (_polling) return;
        _polling = true;
        Poll(_generation, RetryPolls);
    }

    private static void Poll(int generation, int left)
    {
        Dev.MainThread.PostDelayed(RetrySeconds, () =>
        {
            if (generation != _generation) return;
            Pending.Retry("chainloader ready");
            if (Pending.PendingCount == 0)
            {
                _polling = false;
                return;
            }
            if (left <= 1)
            {
                _polling = false;
                Pending.ReportUnbound();
                return;
            }
            Poll(generation, left - 1);
        });
    }
}
#endif
