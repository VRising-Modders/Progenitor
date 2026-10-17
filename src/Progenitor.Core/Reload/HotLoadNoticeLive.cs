using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using BepInEx.Configuration;
using Progenitor.Plugins;

namespace Progenitor.Reload;

/// <summary>
/// Hot-load chat lines, live half (docs/hot-reload.md, "Hot-load messages"). <c>ProgenitorPlugin.Load</c> and
/// <c>Unload</c> report reload-folder plugins here; once a frame this polls <see cref="HotLoadNotices"/> with Bootstrap's
/// last Core reload and sends the line it settles on: on a client, a System line in Progenitor's chat ([Chat]
/// HotLoadMessages); on a server, a System line to every connected player ([HotLoad] Announce in the server's
/// progenitor.cfg). The model's state crosses a Core swap as one plain string in the process's AppDomain data.
/// </summary>
internal static class HotLoadNoticeLive
{
    /// <summary>The AppDomain data key the outgoing Core generation leaves its state under.</summary>
    internal const string ProcessKey = "progenitor.hotload.notices";

    internal const string Section = Networking.ServerChatOptions.HotLoadSection;
    internal const string AnnounceKey = Networking.ServerChatOptions.HotLoadAnnounceKey;

    private const double PollSeconds = 0.25;

    private static readonly object Gate = new();
    private static readonly Stopwatch Clock = Stopwatch.StartNew();
    private static HotLoadNotices? _model;
    private static ConfigEntry<bool>? _announce;
    private static double _nextPoll;
    private static bool _installed;

    private static MethodInfo? _formatLastReload;
    private static int _lookedAtAssemblies;

    private static double Now() => Clock.Elapsed.TotalSeconds;

    /// <summary>The server's [HotLoad] Announce (true until bound).</summary>
    internal static bool Announce => _announce?.Value ?? true;

    internal static void Install()
    {
        if (_installed) return;
        _installed = true;
        string? carried = null;
        try
        {
            carried = AppDomain.CurrentDomain.GetData(ProcessKey) as string;
            AppDomain.CurrentDomain.SetData(ProcessKey, null);
        }
        catch { carried = null; }

        lock (Gate) _model = new HotLoadNotices(Now(), Plugin.Version, carried);
        if (Plugin.IsServerProcess)
        {
            Config.SharedConfig.BindWhenAvailable("[" + Section + "]", file =>
            {
                if (!_installed) return;
                _announce = Config.ConfigHelper.Bind(file, Section, AnnounceKey, true,
                    "Server. Tell every connected player in chat when Core or a mod in BepInEx/progenitor-reload loads, reloads or is removed while the server runs. Edited on the Server tab (Chat).");
            });
        }

        Plugin.Frame -= OnFrame;
        Plugin.Frame += OnFrame;
        Plugin.Logger?.LogInfo("HotLoad: installed (" + (carried == null ? "process start: the first loads are not announced" : "after a Core swap") + ").");
    }

    internal static void Uninstall()
    {
        try { Plugin.Frame -= OnFrame; }
        catch { /* teardown */ }
        HotLoadNotices? model;
        lock (Gate)
        {
            model = _model;
            _model = null;
        }

        if (model != null)
        {
            try { AppDomain.CurrentDomain.SetData(ProcessKey, model.Save(Now())); }
            catch (Exception e) { Plugin.Logger?.LogWarning("HotLoad: the state for the next Core could not be kept: " + e.Message); }
        }

        _announce = null;
        _formatLastReload = null;
        _lookedAtAssemblies = 0;
        _installed = false;
    }

    // ---- plugin events (ProgenitorPlugin) ---------------------------------------------------------------------

    internal static void NoteLoaded(Type pluginType, string id, string name, string version) =>
        Note(pluginType, m => m.Loaded(id, name, version, Now(), Cascade(), Silent()));

    internal static void NoteFailed(Type pluginType, string id, string name, string version) =>
        Note(pluginType, m => m.Failed(id, name, version, Now(), Cascade(), Silent()));

    internal static void NoteUnloaded(Type pluginType, string id, string name, string version, PluginUnloadReason reason) =>
        Note(pluginType, m => m.Unloaded(id, name, version, reason, Now(), Cascade(), Silent()));

    private static void Note(Type pluginType, Action<HotLoadNotices> apply)
    {
        try
        {
            // Reload-folder plugins only: each loads into its own collectible context. A chainloader plugin never reloads.
            if (!(AssemblyLoadContext.GetLoadContext(pluginType.Assembly)?.IsCollectible ?? false)) return;
            lock (Gate)
                if (_model != null) apply(_model);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("HotLoad: a plugin event was not noted: " + e.Message); }
    }

    private static bool Cascade()
    {
        try { return CoreSwapProbe.Probe(null) == CoreSwapProbeResult.InFlight; }
        catch { return false; }
    }

    /// <summary>A client between its connect and the handshake answer: the join's own loads are not hot loads.</summary>
    private static bool Silent()
    {
        if (Plugin.IsServerProcess) return false;
        try
        {
            return Worlds.Client is { IsCreated: true }
                && Networking.Handshake.ClientConnectionHeld
                && !string.Equals(Networking.Handshake.LastClientResult, "ok", StringComparison.Ordinal);
        }
        catch { return false; }
    }

    // ---- the line ---------------------------------------------------------------------------------------------

    private static void OnFrame()
    {
        double now = Now();
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        string? line;
        try
        {
            var status = ReadStatus();
            lock (Gate) line = _model?.Poll(now, status);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("HotLoad: poll threw: " + e.Message);
            return;
        }

        if (line != null) Send(line);
    }

    private static void Send(string line)
    {
        if (Plugin.IsServerProcess)
        {
            string text = HotLoadNoticeText.ServerLine(line);
            if (!Announce)
            {
                Plugin.Logger?.LogInfo("HotLoad: " + line + " (not announced: [HotLoad] Announce is off)");
                return;
            }

            int sent = 0;
            try { sent = Progenitor.Chat.Chat.Broadcast(text); }
            catch (Exception e) { Plugin.Logger?.LogWarning("HotLoad: the broadcast threw: " + e.Message); }
            Plugin.Logger?.LogInfo("HotLoad: " + line + " (sent to " + sent + " player" + (sent == 1 ? "" : "s") + ")");
            return;
        }

        bool shown = false;
        string why = "";
        try { shown = Progenitor.Chat.ChatView.AddHotLoadLine(line, out why); }
        catch (Exception e) { why = e.Message; }
        Plugin.Logger?.LogInfo("HotLoad: " + line + (shown ? " (shown in chat)" : " (not shown: " + why + ")"));
    }

    /// <summary>Bootstrap's <c>CoreReloadStatus.FormatLastReload()</c> and <c>CoreLoader.IsReloading</c>; null without Bootstrap.</summary>
    private static HotLoadCoreStatus? ReadStatus()
    {
        var method = FormatLastReload();
        if (method == null) return null;
        string? text;
        try { text = method.Invoke(null, Array.Empty<object>()) as string; }
        catch { return null; }
        bool inFlight;
        try { inFlight = CoreSwapProbe.Probe(null) == CoreSwapProbeResult.InFlight; }
        catch { inFlight = false; }
        return HotLoadCoreStatus.Parse(text, inFlight);
    }

    private static MethodInfo? FormatLastReload()
    {
        if (_formatLastReload != null) return _formatLastReload;
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        if (assemblies.Length == _lookedAtAssemblies) return null;
        _lookedAtAssemblies = assemblies.Length;
        foreach (var asm in assemblies)
        {
            try
            {
                if (!string.Equals(asm.GetName().Name, "Progenitor.Bootstrap", StringComparison.Ordinal)) continue;
                var t = asm.GetType("Progenitor.Bootstrap.CoreReloadStatus");
                _formatLastReload = t?.GetMethod("FormatLastReload", BindingFlags.Public | BindingFlags.Static, null, Type.EmptyTypes, null);
                return _formatLastReload;
            }
            catch { /* the next assembly */ }
        }

        return null;
    }
}
