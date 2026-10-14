using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.Json;

namespace Progenitor.Dev.Ops;

/// <summary>
/// <c>console</c>: run one command line through the game's own console command dispatcher on the client and return the
/// lines the console printed for it. Client only, main thread, Dev bridge only (loopback plus token, like every op);
/// every command is logged before it runs.
///
/// <para><b>Path.</b> <c>StunConsole.Commands.TryCommand(text, echo)</c>, the call the game's console input box and its
/// debug menu make (Core's "Authenticate as admin" button makes the same call for <c>adminauth</c>). It looks the first
/// word up in the console's command table and runs that command's handler; a client command that needs the server
/// (<c>adminauth</c> sends the game's <c>AdminAuthEvent</c>) sends its own network event, and the server decides. The
/// client's command set is registered whether or not the player has enabled the console in Options, and the console
/// window need not be open: no key, no focus, no device input.</para>
///
/// <para><b>Output.</b> The console window's message list (<c>ConsoleUI_Unity._Messages</c>, created at startup whether
/// or not the console is enabled): the messages added after the command, read <c>waitMs</c> later so a reply that
/// arrives a little after (a server answer) is included. Rich-text color tags are removed.</para>
/// </summary>
internal static class ConsoleOps
{
    internal const string ClientOnlyMessage = "console runs on the client only (the game's client console).";

#if PROGENITOR_TESTS
    internal static object Run(JsonElement args)
    {
        ConsolePlan.Parse(args);
        throw new InvalidOperationException(ClientOnlyMessage);
    }
#else
    internal static object Run(JsonElement args)
    {
        var request = ConsolePlan.Parse(args);
        if (Progenitor.Plugin.IsServerProcess) throw new InvalidOperationException(ClientOnlyMessage);

        var reply = new Dictionary<string, object?>(StringComparer.Ordinal) { ["request"] = request.Describe() };
        var handler = Engine.Console.StunConsole.Commands;
        if (handler == null)
        {
            reply["ok"] = false;
            reply["error"] = "the game's console is not set up yet (StunConsole.Commands is null)";
            return reply;
        }

        bool? known = null;
        try { known = handler.TryFindCommandByName(request.Command, out _); }
        catch (Exception e) { reply["lookupError"] = e.Message; }
        reply["known"] = known;

        var ui = ConsoleWindow();
        reply["outputSource"] = ui == null ? null : "console window";
        IntPtr lastBefore = ui == null ? IntPtr.Zero : LastMessage(ui);

        Plugin.Logger?.LogInfo("Dev console: running the game console command '" + request.Text + "'"
            + (known == false ? " (not in the console's command table)" : ""));
        try { handler.TryCommand(request.Text, request.Echo); }
        catch (Exception e)
        {
            reply["ok"] = false;
            reply["error"] = "the command threw: " + e.Message;
            reply["output"] = ui == null ? null : Output(ui, lastBefore, out _);
            Plugin.Logger?.LogWarning("Dev console: '" + request.Text + "' threw: " + e.Message);
            return reply;
        }

        if (request.WaitMs == 0) return Finish(reply, request, ui, lastBefore, 0);

        var deferred = new DeferredResult(request.WaitMs + 2_000);
        var clock = Stopwatch.StartNew();
        MainThread.PostDelayed(request.WaitMs / 1000.0, () =>
        {
            try
            {
                if (deferred.BoundGate != null && deferred.BoundGate.ShouldSkip(deferred.BoundTicket)) return;
                deferred.SetResult(Finish(reply, request, ui, lastBefore, clock.ElapsedMilliseconds));
            }
            catch (Exception e)
            {
                reply["ok"] = false;
                reply["error"] = e.Message;
                deferred.SetResult(reply);
            }
        }, deferred);
        return deferred;
    }

    private static Dictionary<string, object?> Finish(Dictionary<string, object?> reply, ConsolePlan.Request request,
        Engine.Console.Unity.ConsoleUI_Unity? ui, IntPtr lastBefore, long waitedMs)
    {
        reply["waitedMs"] = waitedMs;
        List<string>? lines = null;
        if (ui != null)
        {
            try
            {
                lines = Output(ui, lastBefore, out bool truncated);
                reply["truncated"] = truncated;
            }
            catch (Exception e) { reply["outputError"] = e.Message; }
        }
        else reply["outputError"] = "the game's console window is not registered, so its output could not be read";

        reply["output"] = lines;
        bool notFound = reply["known"] is false || (lines != null && ConsolePlan.SaysNotFound(lines));
        reply["ok"] = !notFound;
        if (notFound) reply["error"] = "the console has no command named '" + request.Command + "' on this client";
        Plugin.Logger?.LogInfo("Dev console: '" + request.Text + "' " + (notFound ? "was not found" : "ran")
            + (lines == null ? "" : "; " + lines.Count + " output line(s)"));
        return reply;
    }

    /// <summary>The game's console window (always created by the game at startup), or null.</summary>
    private static Engine.Console.Unity.ConsoleUI_Unity? ConsoleWindow()
    {
        try
        {
            var handler = Engine.Console.StunConsole.UI;
            var ui = handler?._UI;
            return ui?.TryCast<Engine.Console.Unity.ConsoleUI_Unity>();
        }
        catch { return null; }
    }

    private static IntPtr LastMessage(Engine.Console.Unity.ConsoleUI_Unity ui)
    {
        try
        {
            var list = ui._Messages;
            if (list == null || list.Count == 0) return IntPtr.Zero;
            var last = list[list.Count - 1];
            return last == null ? IntPtr.Zero : last.Pointer;
        }
        catch { return IntPtr.Zero; }
    }

    /// <summary>The rows of every message added after <paramref name="lastBefore"/>: written rows, then the rows still queued.</summary>
    private static List<string> Output(Engine.Console.Unity.ConsoleUI_Unity ui, IntPtr lastBefore, out bool truncated)
    {
        truncated = false;
        var list = ui._Messages;
        if (list == null) return new List<string>();
        int count = list.Count;
        var ids = new List<IntPtr>(count);
        for (int i = 0; i < count; i++)
        {
            var m = list[i];
            ids.Add(m == null ? IntPtr.Zero : m.Pointer);
        }

        var rows = new List<string?>();
        for (int i = ConsolePlan.NewStart(ids, lastBefore); i < count; i++)
        {
            var m = list[i];
            if (m == null) continue;
            var written = m.WrittenRows;
            if (written != null)
                for (int r = 0; r < written.Count; r++) rows.Add(written[r]);
            var queued = m.UnwrittenRows;
            if (queued != null && queued.Count > 0)
            {
                var arr = queued.ToArray();
                for (int r = 0; r < arr.Length; r++) rows.Add(arr[r]);
            }
        }

        return ConsolePlan.Lines(rows, out truncated);
    }
#endif
}
