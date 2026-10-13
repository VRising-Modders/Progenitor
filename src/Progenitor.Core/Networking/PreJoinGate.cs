#if !PROGENITOR_TESTS
using System;
using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Progenitor.Install;
using Progenitor.Patches;
using Progenitor.Ui;
using ProjectM;
using ProjectM.UI;

namespace Progenitor.Networking;

/// <summary>
/// AUTOUPDATE1 (decision 2): the pre-join prompt's live half. A slot-routed prefix on <c>GameHelper.LaunchGame(GameConnect)</c>,
/// the one call every client join ends in (the Join Game browser, Direct Connect, the bridge's <c>connect</c>, a rejoin),
/// hands the launch to <see cref="PreJoinFlow"/> (the game-free state machine) and launches the held <c>GameConnect</c>
/// again, unchanged, when the flow says so. Dialogs are framework <see cref="UiDialog"/>s whose drop (another dialog
/// took the screen, the panel was hidden, Core unloaded) releases the hold. The prefix fails open.
///
/// <para>The server's requirements come from its last handshake (<see cref="ServerRequirementMemory"/>, keyed by
/// <see cref="ServerRequirementMemory.KeyFor"/>: the transport and the address, or the peer-to-peer target's id).</para>
/// </summary>
internal static class PreJoinGate
{
    private const string OwnerId = MyPluginInfo.PLUGIN_GUID + PatchSitePolicy.CoreOwnerSuffix;
    private const double LoadWaitSeconds = 15;
    private static readonly object Owner = new();
    private static readonly object Gate = new();

    private static PatchHandle? _handle;
    private static bool _bypass;
    private static PreJoinFlow? _flow;
    private static ServerRequirementMemory _memory = new();
    private static bool _memoryLoaded;
    private static string _lastKey = "";
    private static string _installError = "";

    internal static bool Installed => _handle != null && !_handle.Disposed;

    /// <summary>AUTOUPDATE2: a join is held by the prompt (the apply window stays closed).</summary>
    internal static bool Holding => _flow?.Holding ?? false;

    private static PreJoinFlow Flow => _flow ??= new PreJoinFlow(new LiveHost());

    internal static void Install()
    {
        if (Plugin.IsServerProcess) return;
        if (Installed) return;
        try
        {
            Patches.Patches.Install();
            MethodInfo? target = typeof(GameHelper).GetMethod(nameof(GameHelper.LaunchGame),
                BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameConnect) }, null);
            _handle = PatchRegistry.PrefixSlot(null, OwnerId, target, Prefix);
            _installError = _handle == null ? "the LaunchGame prefix was refused (see the patches line)" : "";
            Plugin.Logger?.LogInfo(_handle != null
                ? "AutoUpdate: pre-join prompt armed on GameHelper.LaunchGame."
                : "AutoUpdate: pre-join prompt NOT armed: " + _installError);
        }
        catch (Exception e)
        {
            _installError = e.Message;
            Plugin.Logger?.LogWarning("AutoUpdate: pre-join prompt install failed: " + e.Message);
        }

        ClientUpdates.ServerAsked = NoteServerAsked;
        ClientUpdates.ServerCatalog = NoteServerCatalog;
        ModVersionSelect.RangesSource = LiveRanges;
        LoadMemoryAsync();
    }

    internal static void Uninstall()
    {
        // Review item 8: a join held across a Core reload is dropped, and said so.
        try { _flow?.Unload(); }
        catch { /* */ }
        try { _handle?.Dispose(); }
        catch { /* the registry sweep removes it too */ }
        _handle = null;
        ClientUpdates.ServerAsked = null;
        ClientUpdates.ServerCatalog = null;
        ModVersionSelect.RangesSource = null;
        _flow = null;
        _bypass = false;
    }

    private static void LoadMemoryAsync()
    {
        string? root = null;
        try { root = BepInEx.Paths.BepInExRootPath; }
        catch { root = null; }
        if (string.IsNullOrEmpty(root)) return;
        Task.Run(() =>
        {
            var memory = ClientUpdateInputs.ReadRequirements(root!);
            lock (Gate)
            {
                if (_memoryLoaded) return;
                _memory = memory;
                _memoryLoaded = true;
            }
        });
    }

    /// <summary>The handshake's request: remember what this server asked, under the server this client dialed.</summary>
    private static void NoteServerAsked(IReadOnlyList<ServerRequirement> requirements)
    {
        string key;
        string json;
        lock (Gate)
        {
            key = _lastKey;
            if (key.Length == 0) return;
            _memory.Note(key, requirements, DateTime.UtcNow);
            _memoryLoaded = true;
            json = _memory.ToJson();
        }

        string? root = null;
        try { root = BepInEx.Paths.BepInExRootPath; }
        catch { root = null; }
        if (string.IsNullOrEmpty(root)) return;
        Task.Run(() =>
        {
            var memory = ServerRequirementMemory.Parse(json);
            if (!ClientUpdateInputs.WriteRequirements(root!, memory, out string error))
                Plugin.Logger?.LogWarning("AutoUpdate: could not save the server's requirements: " + error);
        });
        Plugin.Logger?.LogInfo("AutoUpdate: remembered " + requirements.Count + " requirement(s) of " + key + ".");
    }

    /// <summary>
    /// Mod compatibility C3: a verified catalog of the server this client dialed: its per-mod peers and match are merged
    /// into that server's remembered requirements (the request came first) and saved, with the catalog's Version matching
    /// when it carried one (a catalog re-signed after an admin's change replaces the mode the join's request announced).
    /// </summary>
    private static void NoteServerCatalog(IReadOnlyList<ServerRequirement> catalog, string? matching)
    {
        string key;
        string json;
        lock (Gate)
        {
            key = _lastKey;
            if (key.Length == 0 || !_memory.NoteCatalog(key, catalog, matching)) return;
            json = _memory.ToJson();
        }

        string? root = null;
        try { root = BepInEx.Paths.BepInExRootPath; }
        catch { root = null; }
        if (string.IsNullOrEmpty(root)) return;
        Task.Run(() =>
        {
            var memory = ServerRequirementMemory.Parse(json);
            if (!ClientUpdateInputs.WriteRequirements(root!, memory, out string error))
                Plugin.Logger?.LogWarning("AutoUpdate: could not save the server's ranges: " + error);
        });
        Plugin.Logger?.LogInfo("AutoUpdate: remembered the ranges of " + catalog.Count + " mod(s) of " + key + ".");
    }

    /// <summary>C3: what the server this client dialed last asked about <paramref name="modId"/>, with its remembered ranges; null when unknown.</summary>
    internal static ServerRequirement? RememberedRequirement(string modId)
    {
        lock (Gate)
        {
            if (_lastKey.Length == 0) return null;
            var reqs = _memory.Find(_lastKey);
            if (reqs == null) return null;
            foreach (var r in reqs)
                if (r != null && string.Equals(r.ModId, modId, StringComparison.OrdinalIgnoreCase)) return r.Copy();
        }

        return null;
    }

    /// <summary>
    /// C3: the kept-copy choice's range input for the server being joined: its Version matching (request bits), its
    /// remembered ranges and match, and each copy's own range (the loaded declaration, else the copy's verified
    /// inspection).
    /// </summary>
    private static KeptCopyRanges LiveRanges()
    {
        string? root = null;
        try { root = BepInEx.Paths.BepInExRootPath; }
        catch { root = null; }
        var cache = string.IsNullOrEmpty(root) ? null : new VerifiedManifestCache(root!);
        return new KeptCopyRanges
        {
            Mode = ClientUpdates.ServerMatchingMode(),
            Server = RememberedRequirement,
            CopyRange = (id, version, sourceDetail) =>
            {
                try
                {
                    var loaded = VersionMatching.DeclarationSource?.Invoke(id);
                    if (loaded != null && string.Equals(loaded.Version, version, StringComparison.OrdinalIgnoreCase) && loaded.EffectiveRange.Length > 0)
                        return loaded.EffectiveRange;
                    return KeptCopyRanges.FromVerified(cache, sourceDetail, version);
                }
                catch
                {
                    return null;
                }
            },
        };
    }

    private static string KeyOf(GameConnect gc)
    {
        try
        {
            var a = gc.ConnectAddress;
            string host = "";
            string protocol = "";
            string eos = "";
            ulong steam = 0;
            try { host = a.Address.ToString() ?? ""; } catch { host = ""; }
            try { protocol = a.Protocol.ToString() ?? ""; } catch { protocol = ""; }
            try { steam = a.TargetSteamId; } catch { steam = 0; }
            try { eos = a.TargetEosId.ToString() ?? ""; } catch { eos = ""; }
            return ServerRequirementMemory.KeyFor(protocol, host, a.Port, steam, eos);
        }
        catch { return ""; }
    }

    /// <summary>The slot body (main thread, inside <c>LaunchGame</c>). False holds the launch.</summary>
    private static bool Prefix(object? instance, object?[] args)
    {
        try
        {
            if (args == null || args.Length == 0 || args[0] is not GameConnect gc) return true;
            string key = KeyOf(gc);
            lock (Gate) _lastKey = key;
            if (_bypass) return true;
            bool join;
            try { join = gc.CreateClient && !gc.ShouldHost; }
            catch { join = false; }
            if (!join) return true;
            return Flow.OnLaunch(gc, key, () => DecideFor(key));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning("AutoUpdate: pre-join check threw (" + e.Message + "); joining.");
            return true;
        }
    }

    internal static PreJoinDecision DecideFor(string key)
    {
        IReadOnlyList<ServerRequirement>? reqs;
        lock (Gate) reqs = key.Length > 0 ? _memory.Find(key) : null;
        return PreJoinOffers.Decide(ClientUpdates.Report, reqs, ClientUpdates.Mode, ClientUpdates.PromptOn, key, Flow.Dismissed,
            ClientUpdates.CandidateRange);
    }

    private sealed class LiveHost : IPreJoinHost
    {
        public bool Ask(PreJoinQuestion q, Action<string> answered, Action dropped)
        {
            List<(DialogButton, Action)>? extras = null;
            if (q.ExtraCaption.Length > 0)
                extras = new List<(DialogButton, Action)>
                {
                    (new DialogButton("prejoin-extra", q.ExtraCaption, DialogButtonRole.Option), () => answered(PreJoinFlow.KeyExtra)),
                };
            var dialog = UiDialog.Open(
                title => Ui.Ui.Panel(title, Owner, "progenitor"),
                "prejoin", q.Kind, q.Key, q.Title, UiRichText.Literal(q.Body),
                q.MainCaption, LabelTone.Ok, q.CancelCaption,
                note: q.Note.Length > 0 ? q.Note : null,
                extras: extras);
            dialog.OnYes(() => answered(PreJoinFlow.KeyMain))
                .OnNo(() => answered(PreJoinFlow.KeyCancel))
                .OnDropped(dropped);
            return dialog.IsOpen;
        }

        public void Launch(object connect, string why)
        {
            if (connect is not GameConnect gc) return;
            try
            {
                GcBeforeLaunch.BeforeOwnLaunch("pre-join " + why);
                _bypass = true;
                try { GameHelper.LaunchGame(gc); }
                finally { _bypass = false; }
                Plugin.Logger?.LogInfo("AutoUpdate: pre-join: launched the held join (" + why + ").");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning("AutoUpdate: pre-join relaunch failed: " + e.Message);
            }
        }

        public bool CanRestartAndJoin(object connect) => connect is GameConnect gc && TargetOf(gc) != null;

        public bool RestartAndJoin(object connect, out string error)
        {
            error = "";
            var target = connect is GameConnect gc ? TargetOf(gc) : null;
            if (target == null)
            {
                error = "this server cannot be joined again automatically (only address joins without a password can)";
                return false;
            }

            var offer = JoinFlowClient.CurrentRestartOffer();
            if (offer == null || !offer.Shown)
            {
                error = "nothing waits for a restart";
                return false;
            }

            var withTarget = new ClientRestartOffer
            {
                Kind = offer.Kind,
                Count = offer.Count,
                Pending = offer.Pending,
                ReturnTo = ClientRestartOffer.ReturnDedicated,
                ReturnName = target.Endpoint,
                Target = target,
                Manual = offer.Manual,
                ManualWhy = offer.ManualWhy,
                Sentence = offer.Sentence,
                Button = offer.Button,
                ConfirmTitle = offer.ConfirmTitle,
                ConfirmBody = offer.ConfirmBody,
                ConfirmYes = offer.ConfirmYes,
            };
            string result = JoinFlowClient.RunRestart(withTarget, dryRun: false);
            if (result is "quitting" or "quitting-manual" or "leaving") return true;
            error = result;
            return false;
        }

        private static JoinTarget? TargetOf(GameConnect? connect)
        {
            if (connect == null) return null;
            var gc = connect.Value;
            try
            {
                if (gc.ConnectPassword.Length > 0) return null; // a password is never stored for a rejoin
                var a = gc.ConnectAddress;
                string protocol = a.Protocol.ToString() ?? "";
                string canonical = JoinTarget.CanonicalProtocol(protocol.Replace("://", ""));
                var target = new JoinTarget { Address = a.Address.ToString() ?? "", Port = a.Port, Protocol = canonical };
                return target.CanReconnect ? target : null;
            }
            catch { return null; }
        }

        public bool BeginUpdate(IReadOnlyList<string> packages, Action<IReadOnlyList<PreparedUpdate>, UpdateRunEnd> done, out string message) =>
            ClientUpdates.BeginUpdate(packages, "pre-join", confirm: false, (prepared, end) => done(prepared, end), out message);

        public void WaitLoaded(IReadOnlyList<HotWrite> writes, Action<bool, string> then)
        {
            var expected = new List<(HotWrite Write, string Sha)>();
            Task.Run(() =>
            {
                foreach (var w in writes) expected.Add((w, w.Sha256.Length > 0 ? w.Sha256 : ClientUpdateInputs.Sha256Of(w.Path) ?? ""));
            }).ContinueWith(_ => Dev.MainThread.Post(() => Poll(expected, DateTime.UtcNow.AddSeconds(LoadWaitSeconds), then), Owner),
                TaskScheduler.Default);
        }

        private static void Poll(List<(HotWrite Write, string Sha)> expected, DateTime until, Action<bool, string> then)
        {
            var waiting = new List<string>();
            foreach (var (w, sha) in expected)
            {
                if (!BootstrapLink.TryCaptureAdmissionSnapshot(w.PluginId, null, null, out var v) || v == null)
                {
                    // An older Bootstrap cannot say which image is loaded: this one cannot be confirmed.
                    waiting.Add(w.Name + " (the loader cannot report it)");
                    continue;
                }

                bool loaded = v.PluginLoaded && sha.Length > 0
                    && string.Equals(v.PluginImageSha256, sha, StringComparison.OrdinalIgnoreCase);
                if (!loaded) waiting.Add(w.Name);
            }

            if (waiting.Count == 0)
            {
                then(true, "");
                return;
            }

            if (DateTime.UtcNow >= until)
            {
                then(false, "Not loaded after " + (int)LoadWaitSeconds + " seconds: " + string.Join(", ", waiting) + ".");
                return;
            }

            Dev.MainThread.PostDelayed(0.25, () => Poll(expected, until, then), Owner);
        }

        public void Log(string line) => Plugin.Logger?.LogInfo(line);

        public bool UpdateWriting => ClientUpdates.Writing;

        public void After(double seconds, Action action) => Dev.MainThread.PostDelayed(seconds, action, Owner);
    }

    internal static Dictionary<string, object?> Describe()
    {
        Dictionary<string, object?> d;
        try { d = Flow.Describe(); }
        catch { d = new Dictionary<string, object?>(); }
        lock (Gate)
        {
            d["armed"] = Installed;
            d["installError"] = _installError;
            d["lastServer"] = _lastKey;
            d["rememberedServers"] = _memory.Count;
            d["memoryLoaded"] = _memoryLoaded;
        }

        return d;
    }

    /// <summary>Bridge: what the prompt would do for an address join to <c>host:port</c> now (nothing is launched).</summary>
    internal static Dictionary<string, object?> Preview(string host, int port)
    {
        string key = ServerRequirementMemory.KeyFor("SteamIPv4", host, port, 0, null);
        var d = DecideFor(key);
        IReadOnlyList<ServerRequirement>? reqs;
        lock (Gate) reqs = _memory.Find(key);
        var list = new List<object?>();
        if (reqs != null)
            foreach (var r in reqs)
                list.Add(new Dictionary<string, object?> { ["id"] = r.ModId, ["version"] = r.Version, ["rule"] = ServerRequirementMemory.RuleWord(r.Rule) });
        var result = d.Describe();
        result["server"] = key;
        result["requirements"] = reqs == null ? null : list;
        return result;
    }

    /// <summary>Bridge: forget the session's Join anyway answers.</summary>
    internal static int ForgetDismissed() => Flow.ForgetDismissed();
}
#endif
