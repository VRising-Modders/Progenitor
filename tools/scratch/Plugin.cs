using System;
using BepInEx;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using Unity.Entities;

namespace Progenitor.Scratch
{
    /// <summary>
    /// Throwaway host for MCP <c>progenitor_exec</c>. Load queues the snippet onto the next game
    /// frame so it does not run inside the reloader's Scan.
    /// </summary>
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    [BepInDependency("progenitor")]
    public sealed class Plugin : BasePlugin
    {
        internal static ManualLogSource? Logger { get; private set; }

        public override void Load()
        {
            Logger = Log;
            Progenitor.Dev.MainThread.Post(RunSnippet);
        }

        public override bool Unload()
        {
            return true;
        }

        private void RunSnippet()
        {
            try
            {
                Log.LogInfo("scratch: begin");
                var result = Snippet.Run();
                Log.LogInfo("scratch: result: " + (result?.ToString() ?? "null"));
            }
            catch (Exception e)
            {
                // Logged BEFORE the error line: the MCP exec reader stops collecting at "scratch: error".
                if (IsTypeInitializerFailure(e))
                {
                    Log.LogError("scratch: PROCESS PIN: a type initializer threw inside this snippet; the runtime caches that exception and it roots this plugin's allocator and the Core generation it referenced until the process restarts (evidence 219). The next Core swap will be refused.");
                }
                Log.LogError("scratch: error: " + e);
            }
        }

        /// <summary>
        /// A <see cref="TypeInitializationException"/> anywhere in the chain (directly, as an inner
        /// exception, or inside an <see cref="AggregateException"/>). Research: core-hot-reload.md,
        /// "Pin class: a cached type-initializer exception" (evidence 219).
        /// </summary>
        private static bool IsTypeInitializerFailure(Exception? e)
        {
            for (int depth = 0; e != null && depth < 16; depth++)
            {
                if (e is TypeInitializationException) return true;
                if (e is AggregateException agg)
                {
                    foreach (var inner in agg.InnerExceptions)
                    {
                        if (IsTypeInitializerFailure(inner)) return true;
                    }
                }
                e = e.InnerException;
            }
            return false;
        }
    }
}

/// <summary>Worlds and logging for code compiled into <c>Snippet</c>.</summary>
public static class Scratch
{
    public static World? Server => Progenitor.Worlds.Server;
    public static World? Client => Progenitor.Worlds.Client;
    public static void Log(string message) => Progenitor.Scratch.Plugin.Logger?.LogInfo(message);
}
