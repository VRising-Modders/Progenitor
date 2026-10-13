#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using Progenitor.Dev;
using Progenitor.Install;
using Progenitor.Install.Admission;
using Progenitor.Reload;

namespace Progenitor.Networking;

/// <summary>
/// JOINFLOW4: the live half of the version store (client only): the folders, the reload allowlist, the install records,
/// the loader facts (Bootstrap's admission snapshot), the handshake table, the main thread and the leave watch. Every
/// decision is in <see cref="ModVersionService"/> and <see cref="ModVersionJoin"/>, which the tests drive.
/// </summary>
internal static class ModVersionLive
{
    private static readonly object Owner = new();
    private static bool _installed;

    internal static void Install()
    {
        if (_installed) return;
        try
        {
            var paths = new ModVersionPaths(Paths.BepInExRootPath);
            var env = new ModVersionEnv(paths)
            {
                Allow = (file, sha) =>
                {
                    var decision = ReloadAllowStore.Decide(file, sha);
                    return ReloadAllowPolicy.MayLoad(decision) ? null : ReloadAllowPolicy.Token(decision) + " in " + ReloadAllowPolicy.DisplayPath;
                },
                Books = new ModVersionBooks(paths, LegacyServerMods.RecordPath),
                Info = l => Plugin.Logger?.LogInfo(l),
                Warn = l => Plugin.Logger?.LogWarning(l),
            };
            var engine = new ModVersionEngine(env);
            var service = new ModVersionService(engine, new Runtime(paths));
            ModVersionJoin.Info = l => Plugin.Logger?.LogInfo(l);
            ModVersionJoin.Warn = l => Plugin.Logger?.LogWarning(l);
            ModVersionJoin.Provided = () => Handshake.CopyProvided();
            ModVersionJoin.Post = a => MainThread.Post(a, Owner);
            ModVersionJoin.PostDelayed = (s, a) => MainThread.PostDelayed(s, a, Owner);
            ModVersionJoin.ServerProbe = ServerNow;
            ModVersionJoin.IdleForRestore = Idle;
            ModVersionJoin.PausedForJoin = id =>
            {
                var state = ServerModPause.Current;
                foreach (var p in state.PausedIds) if (string.Equals(p, id, StringComparison.OrdinalIgnoreCase)) return true;
                foreach (var p in state.ResumingIds) if (string.Equals(p, id, StringComparison.OrdinalIgnoreCase)) return true;
                return false;
            };
            ModVersionJoin.JoinInProgress = () =>
                Handshake.ClientConnectionHeld
                && !string.Equals(Handshake.LastClientResult, "ok", StringComparison.Ordinal)
                && !ClientInstallWindow.IsArmed;
            var lines = engine.Reconcile();
            // 2026-10-05: servers no longer send mod files; kept copies a server sent are deleted once, here.
            try
            {
                foreach (var l in service.ForgetServerCopies())
                {
                    lines.Add(l);
                    Plugin.Logger?.LogInfo("JoinFlow: versions: " + l);
                }
            }
            catch (Exception e) { lines.Add("server copies not deleted: " + e.Message); }
            try
            {
                foreach (var l in service.RepairMissingActive())
                {
                    lines.Add(l);
                    Plugin.Logger?.LogInfo("JoinFlow: versions: " + l);
                }
            }
            catch (Exception e) { lines.Add("repair failed: " + e.Message); }
            ModVersionJoin.SetReconcile(lines);
            ModVersionJoin.Service = service;
            Handshake.ClientConnectionEnded += ModVersionJoin.OnConnectionEnded;
            _installed = true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("JoinFlow: versions: keeping versions is not available: " + e.Message);
        }
    }

    /// <summary>
    /// AUTOUPDATE3: the version store over this server's own BepInEx folder (<c>progenitor-versions</c> beside its
    /// <c>progenitor-reload</c>): the server's auto-updates park the replaced copy here and swap the new one in, exactly as the
    /// client's do, so the previous version is kept and a failed load is rolled back. No join wiring (a server joins nothing).
    /// An interrupted swap is finished or undone first (<see cref="ModVersionEngine.Reconcile"/>). Null when it cannot be built.
    /// </summary>
    internal static ModVersionService? CreateServerService(Action<string>? info, Action<string>? warn)
    {
        try
        {
            var paths = new ModVersionPaths(Paths.BepInExRootPath);
            var env = new ModVersionEnv(paths)
            {
                Allow = (file, sha) =>
                {
                    var decision = ReloadAllowStore.Decide(file, sha);
                    return ReloadAllowPolicy.MayLoad(decision) ? null : ReloadAllowPolicy.Token(decision) + " in " + ReloadAllowPolicy.DisplayPath;
                },
                // A server keeps no record of mods a server sent (that record is a client's); the install ledger is the book.
                Books = new ModVersionBooks(paths, ""),
                Info = l => info?.Invoke(l),
                Warn = l => warn?.Invoke(l),
            };
            var engine = new ModVersionEngine(env);
            var service = new ModVersionService(engine, new Runtime(paths));
            foreach (var line in engine.Reconcile()) info?.Invoke("versions: " + line);
            return service;
        }
        catch (Exception e)
        {
            warn?.Invoke("keeping versions on this server is not available: " + e.Message);
            return null;
        }
    }

    internal static void Uninstall()
    {
        if (!_installed) return;
        _installed = false;
        try { Handshake.ClientConnectionEnded -= ModVersionJoin.OnConnectionEnded; }
        catch { /* teardown */ }
        ModVersionJoin.Service = null;
        try { MainThread.DropOwnedBy(Owner, typeof(ModVersionLive).Assembly); }
        catch { /* teardown */ }
    }

    /// <summary>The joined server: Bootstrap's connect record (address:port), named as the join screens name it.</summary>
    private static (string, string) ServerNow()
    {
        string address = BootstrapLink.LastConnectAddress ?? "";
        int port = BootstrapLink.LastConnectPort;
        string key = address.Length == 0 ? JoinFlowClient.ServerKey : port > 0 ? address + ":" + port : address;
        return (key, JoinFlowClient.ServerLabelNow);
    }

    /// <summary>No client world, and nothing of the join flow is under way.</summary>
    private static bool Idle()
    {
        var w = Worlds.Client;
        if (w != null && w.IsCreated) return false;
        return !JoinFlowClient.BusyForVersions;
    }

    /// <summary>The probes the store asks (file reads are cached by path, size and write time).</summary>
    private sealed class Runtime : IModVersionRuntime
    {
        private readonly ModVersionPaths _paths;
        private readonly Dictionary<string, (long Size, DateTime Write, string Guid, string Version)> _cache = new(StringComparer.OrdinalIgnoreCase);

        public Runtime(ModVersionPaths paths)
        {
            _paths = paths;
        }

        public IReadOnlyList<ModCopy> CopiesOf(string modId)
        {
            var list = new List<ModCopy>();
            Scan(_paths.Reload, SearchOption.TopDirectoryOnly, "reload", modId, list);
            Scan(_paths.Pending, SearchOption.AllDirectories, "pending", modId, list);
            try
            {
                var loader = IL2CPPChainloader.Instance;
                if (loader?.Plugins != null && loader.Plugins.TryGetValue(modId, out var info) && info != null)
                {
                    string location = info.Location ?? "";
                    if (location.Length > 0 && IsUnder(_paths.Plugins, location))
                        list.Add(new ModCopy { Path = location, Folder = "plugins", Version = info.Metadata?.Version?.ToString() ?? "" });
                }
            }
            catch { /* no chainloader view: the reload folder decides */ }

            return list;
        }

        private void Scan(string root, SearchOption depth, string folder, string modId, List<ModCopy> into)
        {
            string[] files;
            try
            {
                if (!Directory.Exists(root)) return;
                files = Directory.GetFiles(root, "*.dll", depth);
            }
            catch { return; }

            foreach (var f in files)
            {
                var (guid, version) = Identify(f);
                if (guid.Length > 0 && string.Equals(guid, modId, StringComparison.OrdinalIgnoreCase))
                    into.Add(new ModCopy { Path = Path.GetFullPath(f), Folder = folder, Version = version });
            }
        }

        private (string, string) Identify(string file)
        {
            try
            {
                var fi = new FileInfo(file);
                if (_cache.TryGetValue(file, out var c) && c.Size == fi.Length && c.Write == fi.LastWriteTimeUtc) return (c.Guid, c.Version);
                string guid = "", version = "";
                if (fi.Length > 0 && fi.Length <= ModVersionLimits.Default.MaxFileBytes)
                {
                    var scan = ArtifactPreflight.Scan(File.ReadAllBytes(file));
                    if (scan.Valid && scan.EntryPoints.Count == 1)
                    {
                        guid = scan.EntryPoints[0].Guid ?? "";
                        version = scan.EntryPoints[0].Version ?? "";
                    }
                }

                _cache[file] = (fi.Length, fi.LastWriteTimeUtc, guid, version);
                return (guid, version);
            }
            catch { return ("", ""); }
        }

        public string? ManagedBy(string modId)
        {
            string root = _paths.BepInExRoot;
            foreach (var tool in new[] { "r2modman", "Thunderstore Mod Manager", "Gale" })
                if (root.IndexOf(tool, StringComparison.OrdinalIgnoreCase) >= 0) return tool;
            return null;
        }

        public bool UnsafeHarmony(string modId) => UnloadBlocker(modId).Length > 0;

        // A file stem no plugin uses: Bootstrap's PriorGenerationPinned also asks whether a plugin context with the
        // candidate's stem exists, and with no candidate it asks about the LOADED file's stem, which is the plugin's
        // own live context; that read "pinned" for every loaded reload plugin, so every keep took the restart route
        // (live 2026-10-04, HotDemo). With this stem only the process-wide causes remain: a previous Core context
        // still alive, a retiring plugin context still latched, a detour-cache pin.
        private const string NoCandidate = "progenitor-versions-no-candidate.dll";

        public string UnloadBlocker(string modId)
        {
            // Fails closed: no snapshot reads as "cannot unload safely", which takes the restart route.
            if (BootstrapLink.AdmissionSnapshotCapability < 1)
                return "this Bootstrap predates the admission snapshot (HOT2), so Progenitor cannot tell whether the reloader can unload it";
            if (!BootstrapLink.TryCaptureAdmissionSnapshot(modId, NoCandidate, null, out var v) || v == null)
                return "Bootstrap's admission snapshot could not be read";
            if (v.UnsafeHarmonyMarked) return ModVersionRouteText.UnsafeHarmony;
            if (v.ProcessLifetimeMarked) return ModVersionRouteText.ProcessLifetime;
            if (v.PriorGenerationPinned)
                return "a previous Core or plugin generation is still alive in this process, so the reloader may not load a new copy until V Rising restarts";
            return "";
        }

        public int Dependents(string modId)
        {
            try { return Progenitor.Plugins.ModRegistry.RunningDependants(modId).Count; }
            catch { return 1; }
        }

        public string DependentNames(string modId)
        {
            try { return string.Join(", ", Progenitor.Plugins.ModRegistry.RunningDependants(modId).ConvertAll(d => d.Name.Length > 0 ? d.Name : d.Id)); }
            catch (Exception e) { return "they could not be listed: " + e.Message; }
        }

        public bool LoaderBusy
        {
            get
            {
                if (!BootstrapLink.TryCaptureAdmissionSnapshot("", null, null, out var v) || v == null) return false;
                return v.LoaderBusy;
            }
        }

        public bool? ReloaderHolds(string modId)
        {
            // Bootstrap's ReloaderHost.TryDescribeLoadedPlugin (through the HOT2 admission snapshot): the reloader's
            // own list of loaded plugins. An older Bootstrap cannot say.
            if (BootstrapLink.AdmissionSnapshotCapability < 1) return null;
            if (!BootstrapLink.TryCaptureAdmissionSnapshot(modId, null, null, out var v) || v == null) return null;
            return v.PluginLoaded;
        }

        public string? LoadedVersion(string modId)
        {
            foreach (var r in Handshake.CopyProvided())
                if (string.Equals(r.Guid, modId, StringComparison.OrdinalIgnoreCase) && r.Version.Length > 0) return r.Version;
            try
            {
                foreach (var mod in ModManifest.EnumerateLoaded())
                    if (string.Equals(mod.Id, modId, StringComparison.OrdinalIgnoreCase) && mod.Version.Length > 0) return mod.Version;
            }
            catch { /* not loaded */ }

            return null;
        }

        public (string Level, string Rule) Declared(string modId)
        {
            foreach (var r in Handshake.CopyProvided())
                if (string.Equals(r.Guid, modId, StringComparison.OrdinalIgnoreCase) && r.Declared)
                    return (r.Level.ToString(), r.VersionRule.ToString());
            return ("", "");
        }

        public string? ServerRecordHost(string fileName)
        {
            foreach (var r in LegacyServerMods.Installed())
                if (string.Equals(r.FileName, fileName, StringComparison.OrdinalIgnoreCase))
                    return r.Host.Length > 0 ? r.Host : r.HostKey;
            return null;
        }

        private static bool IsUnder(string root, string path)
        {
            try
            {
                string r = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
                return Path.GetFullPath(path).StartsWith(r, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }
    }
}
#endif
