using System;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime;

namespace Progenitor;

/// <summary>
/// Windowless extra clients. A client started with <c>-batchmode</c> has no window but keeps its
/// GPU device, so HDRP and the UI shaders load. The game itself, however, reads
/// <c>Application.isBatchMode</c> as "this process is a server": <c>UIEntryPoint.Awake</c> skips the
/// main menu and calls <c>SceneManager.LoadScene(1)</c>, and <c>GameBootstrap.Start</c> takes its
/// host-server branch and throws (native client <c>ProjectM.UI.UIEntryPoint$$Awake</c> and
/// <c>ProjectM.GameBootstrap$$Start</c>; live run 2026-09-24: "Calling LoadScene 1", "Check Host
/// Server", <c>Failed to startup</c>).
///
/// <para>When the extra-client launcher asks for it (<c>PROGENITOR_HEADLESS=1</c> together with a
/// non-empty <c>PROGENITOR_INSTANCE</c>), this re-registers the IL2CPP internal call behind
/// <c>Application.isBatchMode</c> so managed game code sees <c>false</c>. Unity's native engine
/// does not go through the internal call, so the process stays windowless. IL2CPP resolves an
/// internal call on first use and caches the pointer at the call site, so this must run before the
/// first scene's <c>Awake</c>; Bootstrap's <c>Load</c> is the earliest Progenitor code in the
/// process. The primary Steam client and servers never install it.</para>
/// </summary>
internal static class HeadlessClient
{
    public const string EnvHeadless = "PROGENITOR_HEADLESS";
    public const string EnvInstance = "PROGENITOR_INSTANCE";
    private const string BatchModeIcall = "UnityEngine.Application::get_isBatchMode()";

    /// <summary>True once the internal call answers <c>false</c> for this process.</summary>
    public static bool Installed { get; private set; }

    /// <summary>True when this process really runs with <c>-batchmode</c> (no window).</summary>
    public static bool ReallyBatchMode { get; private set; }

    public static void TryInstall(bool isServerProcess, Action<string> log)
    {
        bool batch = HeadlessClientPolicy.HasBatchModeArg(Environment.GetCommandLineArgs());
        ReallyBatchMode = batch;
        if (!HeadlessClientPolicy.ShouldInstall(isServerProcess, Environment.GetEnvironmentVariable(EnvInstance),
                Environment.GetEnvironmentVariable(EnvHeadless), batch))
            return;
        try
        {
            IntPtr original = IL2CPP.il2cpp_resolve_icall(BatchModeIcall);
            IntPtr replacement = GetReplacementPointer();
            // Process-lifetime name buffer: never freed, in case the runtime keeps the pointer.
            IL2CPP.il2cpp_add_internal_call(Marshal.StringToHGlobalAnsi(BatchModeIcall), replacement);
            IntPtr now = IL2CPP.il2cpp_resolve_icall(BatchModeIcall);
            Installed = now == replacement;
            // Before the first scene loads textures and HDRP sizes its buffers: low texture mips and a
            // frame cap from the start. The Dev plugin's HeadlessProfile keeps re-applying these.
            try
            {
                UnityEngine.QualitySettings.masterTextureLimit = 4;
                UnityEngine.QualitySettings.vSyncCount = 0;
                UnityEngine.Application.targetFrameRate = 20;
            }
            catch (Exception e) { log("HeadlessClient: early quality settings failed: " + e.Message); }
            log($"HeadlessClient: {BatchModeIcall} original=0x{original.ToInt64():X} now=0x{now.ToInt64():X} " +
                (Installed
                    ? "-> game code sees isBatchMode=false (process stays windowless)"
                    : "-> re-registration did not take; the client will boot as a server and never reach the menu"));
        }
        catch (Exception e)
        {
            log("HeadlessClient: install failed: " + e.Message);
        }
    }

    private static unsafe IntPtr GetReplacementPointer()
    {
        delegate* unmanaged[Cdecl]<byte> fn = &NotBatchMode;
        return (IntPtr)fn;
    }

    // Native bool is one byte returned in AL; the original takes no arguments.
    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static byte NotBatchMode() => 0;
}
