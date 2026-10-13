using System;
using System.Collections.Generic;
using Progenitor.Plugins;

namespace Progenitor.Sample;

/// <summary>
/// Job FSAMP. One log helper for the FSAMP commands, and the ring the <c>Ctrl+F9</c> panel
/// (<see cref="SampleLogPanel"/>) reads.
///
/// <b>Why a helper at all.</b> Every line the Sample writes is greppable as
/// <c>Sample &lt;area&gt;: &lt;message&gt;</c> — <c>Sample epoch:</c>, <c>Sample patchdemo:</c>,
/// <c>Sample access:</c>, <c>Sample godmode:</c>, <c>Sample logpanel:</c> — so
/// <c>docs/bridge-ops.md</c>'s live checks can name the exact line to expect for each command. The
/// ring exists because the panel has to show something a player can read on the client without
/// tailing a file; it holds only lines written through this helper, which is every FSAMP feature
/// and nothing else (the older <c>.ember</c> / <c>.rat</c> lines go straight to
/// <c>Plugin.Logger</c> and are deliberately left alone).
///
/// Game-free by construction: the BepInEx call is the one thing behind
/// <c>!PROGENITOR_TESTS</c>, so the tests assert the formatted line and the ring's eviction
/// without a V Rising install.
/// </summary>
internal static class SampleLog
{
    /// <summary>How many lines the ring keeps. The panel shows the last <see cref="PanelLines"/>.</summary>
    internal const int Capacity = 16;

    /// <summary>How many of the kept lines the <c>Ctrl+F9</c> panel renders.</summary>
    internal const int PanelLines = 3;

    private static readonly object Gate = new();
    private static readonly List<string> Lines = new(Capacity);

    /// <summary>Test/diagnostic sink. Set by the tests; never set in the game.</summary>
    internal static Action<string>? Sink;

    /// <summary>Format one line without writing it. <c>Sample epoch: stamped …</c>.</summary>
    internal static string Format(string area, string message) =>
        "Sample " + (area ?? "") + ": " + (message ?? "");

    /// <summary>
    /// Write one <c>Sample &lt;area&gt;: …</c> line to this plugin's BepInEx log and record it in
    /// the ring. Returns the formatted line so a caller can also reply it in chat.
    /// </summary>
    internal static string Info(string area, string message)
    {
        string line = Format(area, message);
        Record(line);

#if !PROGENITOR_TESTS
        try { Plugin.Logger?.LogInfo(line); }
        catch { /* the logger is torn down during unload */ }
#endif
        return line;
    }

    /// <summary>
    /// Record a line that some other Sample file has already written to the log itself
    /// (<see cref="SampleUi"/>'s <c>Sample: ui …</c> lines are the case this exists for), so the
    /// <c>Ctrl+F9</c> panel has client-side lines to show. The ring only ever holds lines from
    /// <em>this</em> process: a server command's line is in the server's ring, not the client's.
    /// </summary>
    internal static void Record(string line)
    {
        if (string.IsNullOrEmpty(line)) return;
        lock (Gate)
        {
            Lines.Add(line);
            while (Lines.Count > Capacity) Lines.RemoveAt(0);
        }

        try { Sink?.Invoke(line); }
        catch { /* a test sink must never break the plugin */ }
    }

    /// <summary>The last <paramref name="count"/> recorded lines, oldest first.</summary>
    internal static IReadOnlyList<string> Recent(int count)
    {
        if (count <= 0) return Array.Empty<string>();
        lock (Gate)
        {
            int take = Math.Min(count, Lines.Count);
            var result = new string[take];
            Lines.CopyTo(Lines.Count - take, result, 0, take);
            return result;
        }
    }

    /// <summary>
    /// The <c>unload</c> area's message, in one place so a test can pin it. The rule is
    /// <c>docs/tutorial.md</c> §7 / <c>docs/modules.md</c> "Unload reason": <c>Removal</c> is the
    /// only sweeping reason, because a wrong keep is recoverable (the plugin comes back, or an
    /// admin clears it by command) and a wrong sweep is not. <c>PluginUnloadReasonPolicy</c> is
    /// that table in one call - the Sample never re-derives it from the enum.
    /// </summary>
    internal static string UnloadMessage(PluginUnloadReason reason) =>
        "reason=" + reason + " -> " + (PluginUnloadReasonPolicy.KeepsState(reason) ? "keep" : "sweep")
        + " plain game state; Core-owned handles are swept on every reason either way";

    /// <summary>The full formatted unload line, for a test that greps the log.</summary>
    internal static string UnloadLine(PluginUnloadReason reason) => Format("unload", UnloadMessage(reason));

    /// <summary>Drop every recorded line. Unload does this so a swap does not show stale text.</summary>
    internal static void Clear()
    {
        lock (Gate) Lines.Clear();
        Sink = null;
    }
}
