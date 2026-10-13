using System;
using System.Collections.Generic;
using BepInEx.Configuration;

namespace Progenitor.Config;

/// <summary>
/// The game-free half of <c>SharedConfig</c>: sections that asked to bind on the shared
/// <c>progenitor.cfg</c> before it could be found, held until it can. Every state change
/// is one log line per section, so a section is never skipped silently. The resolver and log sink
/// are injected; <c>SharedConfig</c> supplies the Bootstrap handover and chainloader
/// lookup, and the tests supply a plain <see cref="ConfigFile"/>.
///
/// <para>Holds Core delegates, so the owner drops it (<see cref="Clear"/>) at Core uninstall. It
/// never keeps the <see cref="ConfigFile"/> it binds into.</para>
/// </summary>
internal sealed class DeferredBinds
{
    private readonly object _gate = new();
    private readonly Func<ConfigFile?> _resolve;
    private readonly Action<string> _log;
    private readonly List<Pending> _pending = new();

    internal DeferredBinds(Func<ConfigFile?> resolve, Action<string> log)
    {
        _resolve = resolve ?? throw new ArgumentNullException(nameof(resolve));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    /// <summary>Sections still waiting for the file.</summary>
    internal int PendingCount
    {
        get { lock (_gate) return _pending.Count; }
    }

    /// <summary>
    /// Run <paramref name="bind"/> on the shared file now when it resolves; otherwise queue it and
    /// log one line naming <paramref name="section"/>. A section already queued is not queued
    /// again (ModManifest.Install runs twice per generation). Returns true when the bind ran now.
    /// A throwing bind is logged and counts as having run: retrying it would throw again.
    /// </summary>
    internal bool Request(string section, Action<ConfigFile> bind)
    {
        if (string.IsNullOrEmpty(section)) throw new ArgumentException("Section is required.", nameof(section));
        if (bind == null) throw new ArgumentNullException(nameof(bind));

        var file = TryResolve();
        if (file != null)
        {
            Run(section, bind, file, late: null);
            return true;
        }

        lock (_gate)
        {
            for (int i = 0; i < _pending.Count; i++)
            {
                if (!string.Equals(_pending[i].Section, section, StringComparison.Ordinal)) continue;
                _pending[i] = new Pending(section, bind);
                return false;
            }
            _pending.Add(new Pending(section, bind));
        }
        _log(DeferredLog(section));
        return false;
    }

    /// <summary>
    /// Bind every queued section into <paramref name="offered"/>, or into the resolved file when
    /// none is offered. Returns how many ran; 0 when the file is still unavailable.
    /// </summary>
    internal int Retry(string reason, ConfigFile? offered = null)
    {
        List<Pending> run;
        lock (_gate)
        {
            if (_pending.Count == 0) return 0;
        }
        var file = offered ?? TryResolve();
        if (file == null) return 0;
        lock (_gate)
        {
            run = new List<Pending>(_pending);
            _pending.Clear();
        }
        for (int i = 0; i < run.Count; i++)
            Run(run[i].Section, run[i].Bind, file, reason ?? "");
        return run.Count;
    }

    /// <summary>
    /// Log one line per section still waiting, saying its defaults stay in effect. The sections
    /// stay queued: a later <see cref="Retry"/> (a shared-file reload) can still bind them.
    /// </summary>
    internal int ReportUnbound()
    {
        List<string> sections;
        lock (_gate)
        {
            sections = new List<string>(_pending.Count);
            for (int i = 0; i < _pending.Count; i++) sections.Add(_pending[i].Section);
        }
        for (int i = 0; i < sections.Count; i++) _log(UnboundLog(sections[i]));
        return sections.Count;
    }

    /// <summary>Drop every queued bind without running it (Core uninstall).</summary>
    internal void Clear()
    {
        lock (_gate) _pending.Clear();
    }

    internal static string DeferredLog(string section) =>
        "Config: " + section + " not bound yet: " + ConfigHelper.SharedFileName
        + " is not available (no Bootstrap handover, and the chainloader has not listed the Bootstrap plugin yet). "
        + "Defaults apply until it binds.";

    internal static string LateLog(string section, string reason) =>
        "Config: " + section + " bound late from " + ConfigHelper.SharedFileName
        + (string.IsNullOrEmpty(reason) ? "." : " (" + reason + ").");

    internal static string UnboundLog(string section) =>
        "Config: " + section + " could not be bound: " + ConfigHelper.SharedFileName
        + " has not become available. Its defaults stay in effect until it binds.";

    internal static string ThrewLog(string section, string message) =>
        "Config: binding " + section + " threw (" + message + "); its defaults stay in effect.";

    private ConfigFile? TryResolve()
    {
        try { return _resolve(); }
        catch { return null; }
    }

    private void Run(string section, Action<ConfigFile> bind, ConfigFile file, string? late)
    {
        try
        {
            bind(file);
        }
        catch (Exception e)
        {
            _log(ThrewLog(section, e.Message));
            return;
        }
        if (late != null) _log(LateLog(section, late));
    }

    private readonly struct Pending
    {
        internal readonly string Section;
        internal readonly Action<ConfigFile> Bind;

        internal Pending(string section, Action<ConfigFile> bind)
        {
            Section = section;
            Bind = bind;
        }
    }
}
