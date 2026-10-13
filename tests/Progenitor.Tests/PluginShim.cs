using System;
using System.Collections.Generic;

namespace Progenitor;

/// <summary>
/// Stand-in for Core's <c>Plugin.Logger</c> so <see cref="Events.EventBus"/> can compile in this
/// assembly without the game or BepInEx. Tests that care about logged exceptions can assign
/// <see cref="Logger"/>.
/// </summary>
internal static class Plugin
{
    public static TestLogSource? Logger { get; set; }

    /// <summary>Stand-in for Core's per-frame event so linked UI sources compile here.</summary>
#pragma warning disable CS0067 // raised in Core; this assembly only needs the member to compile
    public static event Action? Frame;
#pragma warning restore CS0067
}

/// <summary>Records <c>LogError</c> / <c>LogWarning</c> / <c>LogInfo</c> calls from linked Core sources.</summary>
internal sealed class TestLogSource
{
    private readonly List<string> _errors = new();
    private readonly List<string> _warnings = new();
    private readonly List<string> _infos = new();

    public IReadOnlyList<string> Errors
    {
        get { lock (_errors) return _errors.ToArray(); }
    }

    public IReadOnlyList<string> Warnings
    {
        get { lock (_warnings) return _warnings.ToArray(); }
    }

    public IReadOnlyList<string> Infos
    {
        get { lock (_infos) return _infos.ToArray(); }
    }

    public void Clear()
    {
        lock (_errors) _errors.Clear();
        lock (_warnings) _warnings.Clear();
        lock (_infos) _infos.Clear();
        lock (_diags) _diags.Clear();
    }

    /// <summary>
    /// Job FLATCH2 (R45 finding 31). A logger that throws is not a hypothetical: R44 named
    /// "<c>Plugin.Logger</c> during a Core teardown" as the realistic trigger for the handshake's
    /// mismatch note throwing, and any handler that then reports the failure through the same
    /// logger throws for the same reason. Opt-in, so no existing test changes behavior.
    /// </summary>
    public bool ThrowOnWarning { get; set; }

    public void LogInfo(object data)
    {
        lock (_infos) _infos.Add(data?.ToString() ?? "");
    }

    private readonly List<string> _diags = new();

    /// <summary>The lines written with <c>LogDiag</c> (file-only in Core); each one is also in <see cref="Infos"/>.</summary>
    public IReadOnlyList<string> Diags
    {
        get { lock (_diags) return _diags.ToArray(); }
    }

    /// <summary>Core's <c>DiagnosticLog.LogDiag</c>: an Info line for the log file only, never the console.</summary>
    public void LogDiag(object data)
    {
        string text = data?.ToString() ?? "";
        lock (_diags) _diags.Add(text);
        lock (_infos) _infos.Add(text);
    }

    public void LogWarning(object data)
    {
        if (ThrowOnWarning) throw new InvalidOperationException("test logger is down");
        lock (_warnings) _warnings.Add(data?.ToString() ?? "");
    }

    public void LogError(object data)
    {
        lock (_errors) _errors.Add(data?.ToString() ?? "");
    }
}
