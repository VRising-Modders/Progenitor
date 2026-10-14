using System;
using System.IO;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;

namespace Progenitor.Dev;

/// <summary>
/// The debug bridge. A loopback-only TCP socket speaking newline-delimited JSON, guarded by a
/// token file, exposing reload, logs, chat commands, and inspection to any local client (the MCP
/// server, a script, a person with a terminal). Never ship this DLL on a public server.
/// </summary>
[BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
[BepInDependency("progenitor")]
public sealed class Plugin : BasePlugin, IMainThreadDropTarget
{
    internal static ManualLogSource? Logger { get; private set; }

    /// <summary>
    /// Optional process tag from <c>PROGENITOR_INSTANCE</c>. Extra clients set this so ping/status
    /// can tell 127.0.0.1:27351 from the primary Steam client on 27341. Empty when unset.
    /// </summary>
    internal static string InstanceTag { get; private set; } = "";

    private Bridge? _bridge;

    public override void Load()
    {
        Logger = Log;
        var side = Progenitor.Plugin.IsServerProcess ? "server" : "client";

        // Extra VRising.exe processes share this cfg file, and Bind saves it on every call: three
        // headless clients reloading together collided on it and one Dev plugin failed to load with a
        // sharing violation (live 2026-09-24, its bridge stayed down). Bind without saving, then save
        // once and tolerate a file another instance holds.
        bool saveOnSet = Config.SaveOnConfigSet;
        Config.SaveOnConfigSet = false;
        Ops.CatalogAssetOps.Bind(Config);
        var enabled = Config.Bind("Bridge", "Enabled", true, "Open the loopback debug socket. Dev machines only.");
        var portEntry = Config.Bind("Bridge", "Port", Progenitor.Plugin.IsServerProcess ? 27342 : 27341, "TCP port on 127.0.0.1. Overridden by PROGENITOR_DEV_PORT when that env var is set.");
        var tokenPathEntry = Config.Bind("Bridge", "TokenFile", Path.Combine(Paths.ConfigPath, "progenitor-dev.token"), "File holding the shared secret. Created with a random value if missing. Overridden by PROGENITOR_DEV_TOKEN_FILE when that env var is set.");
#if PROGENITOR_EXTRA_INSTANCE
        var allowExtra = Config.Bind(
            "ExtraInstances",
            "AllowExtraInstances",
            false,
            "DEV ONLY, off by default. When true on a Progenitor test or hosted server, extra VRising.exe instances whose Steam platform id sits in 90000000000020000..+99 skip Steam BeginAuthSession. Always refused on C:\\VRisingServer. Refused when the save/process is not under _testserver or a hosted CloudSaves path (Name containing 'Progenitor' is not enough on its own).");
        var loopbackPort = Config.Bind(
            "ExtraInstances",
            "LoopbackPort",
            0,
            "DEV ONLY, used only when AllowExtraInstances is on and accepted. UDP port of the loopback-only Lidgren listener added next to the Steam transport, so Steam-less extra clients on this machine can join while players keep joining over Steam. 0 = game port + 2.");
#endif
        Config.SaveOnConfigSet = saveOnSet;
        try { Config.Save(); }
        catch (IOException e) { Logger.LogWarning($"Dev config not saved (another instance holds it): {e.Message}"); }

        // Bind still writes the cfg keys for the primary process. Env must win over a stale cfg
        // value: extra VRising.exe processes share the Steam install's BepInEx/config, so using
        // the bound Port would keep them colliding on 27341. Never write env back into cfg.
        var port = ResolvePort(portEntry.Value);
        var tokenPath = ResolveTokenPath(tokenPathEntry.Value);
        InstanceTag = Environment.GetEnvironmentVariable("PROGENITOR_INSTANCE")?.Trim() ?? "";

        if (!enabled.Value)
        {
            Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} loaded on {side}; bridge disabled by config.");
            return;
        }

        // Runtime refusal (threat model surface 10). Everything this plugin installs — the bridge
        // itself, the host capture prefixes, the extra-instance Steam skip — is dev-machine only,
        // so the gate runs before any of it and returns without touching the process.
        var decision = DevGate.Evaluate(BuildGateInputs(port));
        if (!decision.Allowed)
        {
            Log.LogError(DevGate.RefusedLine(decision));
            return;
        }

        if (decision.Warning != null)
            Log.LogWarning($"Dev bridge: {decision.Warning}");

        if (!Progenitor.Plugin.IsServerProcess)
        {
            try { Ops.HostCapture.Install(); }
            catch (Exception e) { Log.LogError($"host_capture install: {e}"); }
            try { Ops.HeadlessProfile.Install(); }
            catch (Exception e) { Log.LogError($"headless profile install: {e}"); }
#if PROGENITOR_EXTRA_INSTANCE
            // R28 finding 28.18: only the SERVER half of the extra-instance Steam skip was behind
            // the define, so "compiled only in the dev configuration" was true of half of it. The
            // client half installs the same dev-only auth shim and belongs behind the same gate.
            try { Ops.ExtraInstanceAuth.Install(); }
            catch (Exception e) { Log.LogError($"extra-auth: install: {e}"); }
#endif
        }
#if PROGENITOR_EXTRA_INSTANCE
        else
        {
            try { Ops.ExtraInstanceServerAuth.Install(allowExtra.Value, loopbackPort.Value); }
            catch (Exception e) { Log.LogError($"extra-auth(server): install: {e}"); }
        }
#endif

        // Regenerated every start; consumers re-read the file (docs/bridge.md "Token file"). The ACL
        // is applied inside this call, to an empty file, before the secret exists (R29 finding 8).
        var token = LoadOrCreateToken(tokenPath, out var tokenAcl);
        // `this` is the owner token for this load. A Core-swap Unload retires the previous
        // Plugin instance and its collectible ALC; posts from this new instance must not
        // inherit that mark (evidence 53).
        var bridge = new Bridge(port, token, InstanceTag, owner: this,
            usersPath: BridgeUserTracker.PathBesideToken(tokenPath));
        try
        {
            bridge.Start();
        }
        catch (Exception e)
        {
            Log.LogError($"bridge listen failed on 127.0.0.1:{port}: {e.Message}");
            try { bridge.Stop(); } catch { }
            return;
        }
        _bridge = bridge;
        Log.LogInfo(DevGate.EnabledLine(decision, DevGate.LoopbackAddress, port, tokenAcl));
        var instanceLabel = InstanceTag.Length == 0 ? "(none)" : InstanceTag;
        // Bind runs on the accept thread (address-in-use retries). Logging "listening"
        // here would fire before listener.Start succeeds (R14 leftover 5). Bridge logs
        // "Progenitor Dev Bridge listening" after the bind; this line is the token/port
        // that MCP needs even while bind is still retrying.
        Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} loaded on {side}; binding 127.0.0.1:{port}. Token file: {tokenPath}. instance={instanceLabel}");
    }

    /// <summary>
    /// MainThread dropped queued work owned by this plugin (Core reload cascade, or a
    /// collectible unload). Free the one-op slot so the reloaded instance is not stuck on
    /// "already running". Deferred waiters are completed by <see cref="MainThread.DropOwnedBy"/>
    /// (drop error; production WorkQueue duck-types this method) or by Unload's
    /// <c>AbortAll</c> backup. The retired mark is this instance / ALC generation, not the
    /// plugin type name.
    /// </summary>
    public void OnMainThreadDropped(Exception exception)
    {
        try { _bridge?.AbortPendingOps(exception?.Message); } catch { }
    }

    public override bool Unload()
    {
        try
        {
            Ops.ChatObserve.Shutdown();
            Ops.WakeOps.Shutdown();
            Ops.HeadlessProfile.Shutdown();
            Bots.BotDriver.Shutdown();
            try { Bots.BotPath.Shutdown(); } catch { /* best-effort */ }
            Ops.CaptureOps.Shutdown();
            // Must run before the collectible context goes: the perf sink is installed into a
            // Core static, so a leftover would pin this plugin ALC and refuse the next swap.
            Ops.PerfOps.Shutdown();
            Ops.GhostOps.Shutdown();
            Ops.TimeOps.Shutdown();
            Ops.WakeOps.Shutdown();
            Ops.CatalogAssetOps.Shutdown();
            Ops.HostCapture.Shutdown();
            // Every key the key op still holds is released before the bridge goes.
            Ops.KeyInjectOps.Shutdown();
            // And every mouse button the mouse op still holds.
            Ops.MouseInjectOps.Shutdown();
            // And the virtual gamepad the nav op added.
            Ops.NavOps.Shutdown();
            // And the synthetic UI input the ui_input op holds (buttons up, exits sent, navigation holds dropped).
            Ops.UiInputOps.Shutdown();
#if PROGENITOR_EXTRA_INSTANCE
            Ops.ExtraInstanceAuth.Shutdown();
            Ops.ExtraInstanceServerAuth.Shutdown();
#endif
        }
        finally
        {
            // Shutdown of an individual op can throw. DropOwnedBy must still run so production
            // queue work completes its DeferredResult with the drop error, then deferred
            // waiters and the one-op slot are failed so the socket thread and the next
            // generation are not stuck (R13 finding 6).
            var bridge = _bridge;
            DevUnload.AbortPendingInFinally(
                abortAll: () => DeferredResult.AbortAll(new InvalidOperationException(MainThreadOpGate.UnloadMessage)),
                abortOps: () => bridge?.AbortPendingOps(),
                stop: () => bridge?.Stop(),
                dropOwnedBy: () => MainThread.DropOwnedBy(this, typeof(Plugin).Assembly));
            _bridge = null;
            InstanceTag = "";
            try { Ops.ConnectOps.Shutdown(); } catch { }
        }
        return true;
    }

    /// <summary>
    /// <c>PROGENITOR_DEV_PORT</c> wins when it is a TCP port in 1..65535. Invalid values log and
    /// fall back to the BepInEx cfg (or its default).
    /// </summary>
    private static int ResolvePort(int configPort)
    {
        var env = Environment.GetEnvironmentVariable("PROGENITOR_DEV_PORT");
        if (string.IsNullOrWhiteSpace(env)) return configPort;
        if (int.TryParse(env, out var parsed) && parsed is > 0 and < 65536)
            return parsed;
        Logger?.LogWarning($"PROGENITOR_DEV_PORT={env} is not a valid TCP port (1..65535); using config {configPort}");
        return configPort;
    }

    /// <summary>
    /// <c>PROGENITOR_DEV_TOKEN_FILE</c> wins when set to a non-empty path. The extra-client
    /// launcher points this at an isolated file under %TEMP% so call.mjs can authenticate that
    /// process without sharing the primary client's token path.
    /// </summary>
    private static string ResolveTokenPath(string configPath)
    {
        var env = Environment.GetEnvironmentVariable("PROGENITOR_DEV_TOKEN_FILE");
        return string.IsNullOrWhiteSpace(env) ? configPath : env.Trim();
    }

    /// <summary>
    /// Generates the shared secret, protects the file, and only then writes the secret into it.
    /// <paramref name="acl"/> reports what the log line should say about the DACL; it is
    /// <see cref="DevGate.TokenAclDefault"/> when the file could not be written at all and the
    /// returned token is session-only.
    ///
    /// <para><b>An existing file is never adopted</b> (R28 finding 20). The old version returned any
    /// pre-existing value of 16 characters or more with no ownership, ACL or entropy check, and
    /// <c>ApplyTokenAcl</c> then locked that file to the current user — which <i>hides</i> a planted
    /// token rather than defeating it, because whoever planted it already knows the value. So: a
    /// fresh 192-bit token every start. Consumers re-read the file — the MCP server
    /// (<c>tools/mcp/server.mjs</c>) and <c>tools/mcp/call.mjs</c> read the token from disk on every
    /// call — so a rotated token needs no restart on their side; a shell that exported the old value
    /// has to re-read it (docs/bridge.md).</para>
    ///
    /// <para><b>The order matters (R29 finding 8).</b> The previous version did
    /// <c>File.Delete</c> then <c>File.WriteAllText</c> and let the caller apply the ACL afterwards.
    /// Two windows fell out of that. The secret sat on disk under the config folder's inherited DACL
    /// for the whole of the ACL call — nine reflection steps plus a DACL read-back — and the threat
    /// this guards is exactly a local process that polls (a third-party BepInEx plugin from its own
    /// <c>Load()</c>, a sync client, a backup agent). And between the delete and the write the path
    /// was unoccupied, so a process that won that race by planting a symlink got the fresh token
    /// written through the link to a location it controlled, after which the restrictive DACL was
    /// applied to <i>the attacker's</i> file.</para>
    ///
    /// <para>So the sequence is: refuse outright if the token path or the temp path is a reparse
    /// point (never write through a link, and never delete one either); create the temp file with
    /// <c>FileMode.CreateNew</c> + <c>FileShare.None</c>, which refuses both a pre-existing file and
    /// a planted link rather than following it; apply and verify the DACL while the file is still
    /// <b>empty</b>; reopen it <c>FileShare.None</c>, which fails if anything grabbed a handle in
    /// between; write the secret; and <c>File.Move(..., overwrite: true)</c> it onto the real path,
    /// which also removes the truncated-read window a plain <c>WriteAllText</c> left for
    /// <c>server.mjs</c> (R29 residual 21.4). The moved file keeps the DACL applied to it.</para>
    /// </summary>
    private static string LoadOrCreateToken(string path, out string acl)
    {
        acl = DevGate.TokenAclDefault;
        var bytes = new byte[24];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        var token = Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

        var temp = path + ".new";
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);

            if (IsReparsePoint(path))
            {
                Logger?.LogError(
                    $"token file {path} is a symlink or junction; refusing to write through it or to "
                    + "delete it. Remove it by hand. Using a session-only token, so the bridge is "
                    + "reachable only by a client that already has this process's token.");
                return token;
            }

            if (IsReparsePoint(temp))
            {
                Logger?.LogError(
                    $"token staging file {temp} is a symlink or junction; refusing to write through "
                    + "it. Remove it by hand. Using a session-only token.");
                return token;
            }

            // A leftover from a crashed start. It is a plain file (checked above), and it never held
            // a token that was published, so deleting it is safe.
            if (File.Exists(temp)) File.Delete(temp);

            // CreateNew + FileShare.None: refuses an existing file and a link, and creates it EMPTY.
            using (new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }

            acl = DevGate.ApplyTokenAcl(
                temp,
                DevGate.RestrictToCurrentUser,
                message => Logger?.LogWarning($"Dev bridge: {message}"),
                DevGate.VerifyRestrictedToCurrentUser);

            // FileShare.None on an Open fails when anything else already holds a handle — including
            // one opened in the gap between creating the empty file and restricting it.
            using (var stream = new FileStream(temp, FileMode.Open, FileAccess.Write, FileShare.None))
            {
                var text = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false).GetBytes(token);
                stream.Write(text, 0, text.Length);
                stream.Flush(flushToDisk: true);
            }

            File.Move(temp, path, overwrite: true);
            return token;
        }
        catch (Exception e)
        {
            acl = DevGate.TokenAclDefault;
            Logger?.LogError($"token file {path}: {e.Message}; using a session-only token");
            try { if (File.Exists(temp)) File.Delete(temp); } catch { }
            return token;
        }
    }

    /// <summary>
    /// True when <paramref name="path"/> exists and carries <see cref="FileAttributes.ReparsePoint"/>
    /// — a symlink or junction. An unreadable attribute reads as "yes": the fence's failure mode has
    /// to be a refusal, not a write through something unknown.
    /// </summary>
    private static bool IsReparsePoint(string path)
    {
        try
        {
            if (!File.Exists(path) && !Directory.Exists(path)) return false;
            return (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0;
        }
        catch (Exception e)
        {
            Logger?.LogWarning($"token file {path}: could not read its attributes ({e.Message}); treating it as a link");
            return true;
        }
    }

    /// <summary>
    /// Collects the disk and environment facts <see cref="DevGate.Evaluate"/> decides on. The
    /// marker file lives beside the token in <c>BepInEx/config</c>, so extra instances (which
    /// share the Steam install's config folder but not its token path) are covered by the same
    /// opt-in. Server exposure is read from the server's own <c>ServerHostSettings.json</c> on
    /// disk, not from <c>SettingsManager</c>: that singleton is not populated yet at Load.
    /// </summary>
    private static DevGateInputs BuildGateInputs(int port)
    {
        var markerPath = Path.Combine(Paths.ConfigPath, DevGate.MarkerFileName);
        string? markerText = null;
        var markerExists = false;
        try
        {
            markerExists = File.Exists(markerPath);
            if (markerExists) markerText = File.ReadAllText(markerPath);
        }
        catch (Exception e)
        {
            Logger?.LogWarning($"Dev bridge: opt-in marker {markerPath}: {e.Message}");
        }

        var isServer = Progenitor.Plugin.IsServerProcess;
        var exposure = new ServerExposure();
        if (isServer)
        {
            string? commandLine = null;
            string? processPath = null;
            try { commandLine = Environment.CommandLine; } catch { /* best effort */ }
            try
            {
                using var proc = System.Diagnostics.Process.GetCurrentProcess();
                processPath = proc.MainModule?.FileName;
            }
            catch { /* best effort */ }

            string? gameRoot = null;
            try { gameRoot = Paths.GameRootPath; } catch { /* best effort */ }
            exposure = DevGate.ReadServerExposure(commandLine, processPath, gameRoot);
        }

        return new DevGateInputs
        {
            MarkerPath = markerPath,
            MarkerExists = markerExists,
            MarkerText = markerText,
            EnvValue = Environment.GetEnvironmentVariable(DevGate.EnvVarName),
            IsServerProcess = isServer,
            BindAddress = DevGate.LoopbackAddress,
            Port = port,
            Server = exposure,
        };
    }
}
