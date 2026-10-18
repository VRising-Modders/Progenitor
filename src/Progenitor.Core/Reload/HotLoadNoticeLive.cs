using System;
using System.Collections.Generic;
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
/// progenitor.cfg), rate-limited by <see cref="HotLoadBroadcastLimiter"/>. The model's state crosses a Core swap as one
/// plain string in the process's AppDomain data.
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
    private static HotLoadBroadcastLimiter _broadcasts = new();
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

        HotLoadNotices model;
        lock (Gate)
        {
            _model = model = new HotLoadNotices(Now(), Plugin.Version, carried);
            _broadcasts = new HotLoadBroadcastLimiter();
        }

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
        Plugin.Logger?.LogInfo("HotLoad: installed (" + (model.InStartup ? "process start: the first loads are not announced" : "after a Core swap") + ").");
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
            // A server line still waiting for the broadcast gap is said by the next generation.
            if (model != null && _broadcasts.Pending) model.CarryForward(_broadcasts.TakePending());
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
        Note(pluginType, (m, cascade, silent) => m.Loaded(id, name, version, Now(), cascade, silent));

    internal static void NoteFailed(Type pluginType, string id, string name, string version) =>
        Note(pluginType, (m, cascade, silent) => m.Failed(id, name, version, Now(), cascade, silent));

    internal static void NoteUnloaded(Type pluginType, string id, string name, string version, PluginUnloadReason reason) =>
        Note(pluginType, (m, cascade, silent) => m.Unloaded(id, name, version, reason, Now(), cascade, silent));

    private static void Note(Type pluginType, Action<HotLoadNotices, bool, bool> apply)
    {
        try
        {
            // Reload-folder plugins only: each loads into its own collectible context. A chainloader plugin never reloads.
            if (!(AssemblyLoadContext.GetLoadContext(pluginType.Assembly)?.IsCollectible ?? false)) return;
            bool cascade = Cascade();
            bool silent = Joining();
            lock (Gate)
                if (_model != null) apply(_model, cascade, silent);
        }
        catch (Exception e) { Plugin.Logger?.LogWarning("HotLoad: a plugin event was not noted: " + e.Message); }
    }

    /// <summary>
    /// The cascade decision (<see cref="HotLoadCoreStatus.IsCascade"/>) on the raw probe answer and status line:
    /// <c>CoreLoader.IsReloading</c> already reads false while the swap unloads and reloads the plugins, the status's
    /// <c>scheduled</c> outcome does not.
    /// </summary>
    private static bool Cascade()
    {
        bool probe;
        try { probe = CoreSwapProbe.Probe(null) == CoreSwapProbeResult.InFlight; }
        catch { probe = false; }
        return HotLoadCoreStatus.IsCascade(probe, ReadStatusLine(), DateTime.UtcNow);
    }

    /// <summary>
    /// A client between its connect and the handshake answer, on a server with Progenitor (a server without it never
    /// answers: once the client knows that, it is not joining any more).
    /// </summary>
    private static bool Joining()
    {
        if (Plugin.IsServerProcess) return false;
        try
        {
            return HotLoadNoticeText.JoinInProgress(
                Worlds.Client is { IsCreated: true },
                Networking.Handshake.ClientConnectionHeld,
                Networking.Handshake.LastClientResult,
                Networking.JoinFlowClient.JoinedWithoutProgenitor);
        }
        catch { return false; }
    }

    // ---- the line ---------------------------------------------------------------------------------------------

    private static void OnFrame()
    {
        double now = Now();
        if (now < _nextPoll) return;
        _nextPoll = now + PollSeconds;
        List<HotLoadSentence>? sentences;
        string? broadcast = null;
        try
        {
            var status = ReadStatus();
            bool hold = Joining();
            lock (Gate)
            {
                sentences = _model?.PollSentences(now, status, hold);
                if (Plugin.IsServerProcess)
                {
                    if (sentences != null) _broadcasts.Offer(sentences);
                    broadcast = _broadcasts.Take(now);
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("HotLoad: poll threw: " + e.Message);
            return;
        }

        if (Plugin.IsServerProcess)
        {
            if (broadcast != null) SendServer(broadcast);
            return;
        }

        string? line = sentences == null ? null : HotLoadNoticeText.Join(sentences);
        if (line == null) return;
        bool shown = false;
        string why = "";
        try { shown = Progenitor.Chat.ChatView.AddHotLoadLine(line, out why); }
        catch (Exception e) { why = e.Message; }
        Plugin.Logger?.LogInfo("HotLoad: " + line + (shown ? " (shown in chat)" : " (not shown: " + why + ")"));
    }

    private static void SendServer(string line)
    {
        if (!Announce)
        {
            Plugin.Logger?.LogInfo("HotLoad: " + line + " (not announced: [HotLoad] Announce is off)");
            return;
        }

        int sent = 0;
        try { sent = Progenitor.Chat.Chat.Broadcast(HotLoadNoticeText.ServerLine(line)); }
        catch (Exception e) { Plugin.Logger?.LogWarning("HotLoad: the broadcast threw: " + e.Message); }
        Plugin.Logger?.LogInfo("HotLoad: " + line + " (sent to " + sent + " player" + (sent == 1 ? "" : "s") + ")");
    }

    /// <summary>Bootstrap's <c>CoreReloadStatus.FormatLastReload()</c>, or null without Bootstrap.</summary>
    private static string? ReadStatusLine()
    {
        var method = FormatLastReload();
        if (method == null) return null;
        try { return method.Invoke(null, Array.Empty<object>()) as string; }
        catch { return null; }
    }

    /// <summary>The status line with <c>CoreLoader.IsReloading</c>; null without Bootstrap.</summary>
    private static HotLoadCoreStatus? ReadStatus()
    {
        string? text = ReadStatusLine();
        if (text == null) return null;
        bool inFlight;
        try { inFlight = CoreSwapProbe.Probe(null) == CoreSwapProbeResult.InFlight; }
        catch { inFlight = false; }
        return HotLoadCoreStatus.Parse(text, inFlight, DateTime.UtcNow);
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
