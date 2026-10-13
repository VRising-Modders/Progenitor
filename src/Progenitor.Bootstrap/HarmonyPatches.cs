using System;
using System.Reflection;
using HarmonyLib;
using ProjectM;
using ProjectM.Auth;
using ProjectM.Shared;
using ProjectM.UI;
using Stunlock.Network;
using Unity.Collections;
using Unity.Entities;
using GameLocalization = Stunlock.Localization.Localization;

namespace Progenitor.Bootstrap;

/// <summary>
/// Applies every Core Harmony patch once per process on Bootstrap's Harmony id. Prefix,
/// postfix, and finalizer bodies only call through <see cref="HarmonySlots"/>. Harmony
/// argument marshaling stays here so collectible Core never contributes a patch
/// <c>MethodInfo</c> (the IL2CPP trampoline pin).
/// </summary>
internal static unsafe class HarmonyPatches
{
    // Job FLATCH1: one entry per site that has already spent its stack trace, cleared with the
    // Core generation by ClearAfterSnapshots. Site names only - never a Core object.
    private static readonly object ThrowLogGate = new();
    private static readonly System.Collections.Generic.HashSet<string> ThrowLogged = new(StringComparer.Ordinal);
    private static Harmony? _harmony;
    private static readonly object LazyGate = new();

    internal static void Install(Harmony harmony)
    {
        if (harmony == null) throw new ArgumentNullException(nameof(harmony));
        _harmony = harmony;
        if (HarmonySlots.OwnsPatches) return;

        int n = 0;
        int formUpdate = PatchSite(HarmonySlotPolicy.SystemBaseUpdate, () => TryPatchAll(harmony, typeof(ManagedUpdateHook)));
        n += formUpdate;
        if (formUpdate > 0) ControlFormService.MarkDestroyArmed();
        n += PatchSite(HarmonySlotPolicy.UnmanagedUpdate, () => TryPatchUnmanaged(harmony));
        n += PatchSite(HarmonySlotPolicy.ChatOnUpdate, () => TryPatchAll(harmony, typeof(ChatHook)));
        n += PatchSite(HarmonySlotPolicy.UserConnected, () => TryPatchAll(harmony, typeof(UserConnectedHook)));
        int formDisconnect = PatchSite(HarmonySlotPolicy.UserDisconnected, () => TryPatchAll(harmony, typeof(UserDisconnectedHook)));
        n += formDisconnect;
        if (formDisconnect > 0) ControlFormService.MarkDisconnectArmed();
        n += PatchSite(HarmonySlotPolicy.DeserializeLoadFile, () => TryPatchAll(harmony, typeof(DeserializeHook)));
        n += PatchSite(HarmonySlotPolicy.TriggerSave, () => TryPatchAll(harmony, typeof(TriggerSaveHook)));
        n += PatchSite(HarmonySlotPolicy.BeginCreateServerWorld, () => TryPatchAll(harmony, typeof(BeginCreateHook)));
        n += PatchSite(HarmonySlotPolicy.StunProcessStart, () =>
        {
            int c = 0;
            c += TryPatchStart(harmony, typeof(StunProcess));
            c += TryPatchStart(harmony, typeof(StunProcess_PInvoke));
            c += TryPatchStart(harmony, typeof(StunProcess_MONO));
            return c;
        });
        n += PatchSite(HarmonySlotPolicy.SetLanguage, () => TryPatchAll(harmony, typeof(SetLanguageHook)));
        if (Plugin.IsServerProcess)
        {
            // Save-safety load guard (jobs FSAVE, FSAVE2). Server-only: the client never runs
            // LoadPersistenceSystemV2. No Core slot — Bootstrap owns the whole body so the
            // guard works in a process with no Core and no plugin loaded.
            //
            // Prefix plans and edits the archetype lists; the postfix performs the destroys, so
            // the only structural change happens after the loader has finished reading. If the
            // original throws, Harmony skips the postfix and nothing is destroyed — which is the
            // vanilla failed load, the fail-safe outcome (R29 findings 17 and 18).
            int guard = PatchSite(HarmonySlotPolicy.CopyDataFromPrefabs, () => TryPatchMethod(
                harmony, typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.CopyDataFromPrefabs),
                prefixType: typeof(SaveSafetyHook), prefix: nameof(SaveSafetyHook.Prefix),
                postfixType: typeof(SaveSafetyHook), postfix: nameof(SaveSafetyHook.Postfix)));
            n += guard;
            if (guard > 0) SaveSafety.MarkArmed();
            int formSave = PatchSite("control.form.serialize", () => TryPatchAll(harmony, typeof(FormSerializeHook)));
            n += formSave;
            if (formSave > 0) ControlFormService.MarkSaveArmed();
            // Guard both the request and the complete load tick. Skipping DeserializeWorldData
            // itself would leave its out arrays uninitialized while OnUpdate continued loading.
            int formLoadRequest = PatchSite("control.form.load.request", () => TryPatchAll(harmony, typeof(FormLoadRequestHook)));
            int formLoadUpdate = PatchSite("control.form.load.update", () => TryPatchAll(harmony, typeof(FormLoadUpdateHook)));
            int formLoadReset = PatchSite("control.form.load.reset", () => TryPatchAll(harmony, typeof(FormLoadResetHook)));
            n += formLoadRequest + formLoadUpdate + formLoadReset;
            if (formLoadRequest > 0 && formLoadUpdate > 0 && formLoadReset > 0) ControlFormService.MarkLoadArmed();
        }
        else
        {
            // Client-only: dedicated-join must clear IsHostingPrivateGame even
            // when Dev has not bound the LaunchGame slot yet.
            n += PatchSite(HarmonySlotPolicy.LaunchGame, () => TryPatchMethod(
                harmony, typeof(GameHelper), nameof(GameHelper.LaunchGame),
                prefixType: typeof(LaunchGameHook), prefix: nameof(LaunchGameHook.Prefix)));
        }

        if (n == 0)
            ProcessHost.Log?.LogError(HarmonySlotPolicy.OwnsPatchesNoneLog);
        else
            ProcessHost.Log?.LogInfo(HarmonySlotPolicy.OwnsPatchesLog(n));
    }

    /// <summary>
    /// Patch a Dev/extra-instance original on first bind. The trampoline then belongs
    /// to Bootstrap for the process lifetime; an empty slot is vanilla. Idempotent.
    /// </summary>
    internal static void EnsureLazy(string site)
    {
        if (string.IsNullOrEmpty(site)) return;
        string key = HarmonySlotPolicy.CanonicalSite(site);
        if (HarmonySlots.OwnsSite(key)) return;
        var harmony = _harmony;
        if (harmony == null) return;
        lock (LazyGate)
        {
            if (HarmonySlots.OwnsSite(key)) return;
            int n;
            try { n = PatchLazy(harmony, key); }
            catch (Exception e)
            {
                ProcessHost.Log?.LogError($"HarmonyPatches: lazy {key} threw: {e}");
                n = 0;
            }
            if (n > 0)
            {
                HarmonySlots.MarkPatched(key);
                ProcessHost.Log?.LogInfo("HarmonyPatches: lazy-installed " + key);
                return;
            }
            ProcessHost.Log?.LogError(HarmonySlotPolicy.VanillaSiteLog(key));
        }
    }

    private static int PatchLazy(Harmony harmony, string key)
    {
        switch (key)
        {
            case HarmonySlotPolicy.LaunchGame:
                return TryPatchMethod(harmony, typeof(GameHelper), nameof(GameHelper.LaunchGame),
                    prefixType: typeof(LaunchGameHook), prefix: nameof(LaunchGameHook.Prefix));
            case HarmonySlotPolicy.GameBootstrapStart:
                return TryPatchMethod(harmony, typeof(GameBootstrap), nameof(GameBootstrap.Start),
                    prefixType: typeof(GameBootstrapStartHook), prefix: nameof(GameBootstrapStartHook.Prefix));
            case HarmonySlotPolicy.GameBootstrapAwake:
                return TryPatchMethod(harmony, typeof(GameBootstrap), nameof(GameBootstrap.Awake),
                    prefixType: typeof(GameBootstrapAwakeHook), prefix: nameof(GameBootstrapAwakeHook.Prefix));
            case HarmonySlotPolicy.SendPacketToUser:
                return TryPatchMethod(harmony, typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.SendPacketToUser),
                    postfixType: typeof(SendPacketToUserHook), postfix: nameof(SendPacketToUserHook.Postfix));
            case HarmonySlotPolicy.GetAuthSessionTicket:
                return TryPatchMethod(harmony, typeof(ClientWorldManager), nameof(ClientWorldManager.GetAuthSessionTicket),
                    prefixType: typeof(AuthTicketHook), prefix: nameof(AuthTicketHook.Prefix));
            case HarmonySlotPolicy.CreateSteamIPv4:
                return TryPatchMethod(harmony, typeof(ConnectAddress), nameof(ConnectAddress.CreateSteamIPv4),
                    postfixType: typeof(SteamAddressHook), postfix: nameof(SteamAddressHook.Postfix));
            case HarmonySlotPolicy.ClientBootstrapConnect:
                return TryPatchMethod(harmony, typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.Connect),
                    prefixType: typeof(ConnectHook), prefix: nameof(ConnectHook.Prefix),
                    postfixType: typeof(ConnectHook), postfix: nameof(ConnectHook.Postfix));
            case HarmonySlotPolicy.UpdateConnectRequest:
                return TryPatchMethod(harmony, typeof(ClientBootstrapSystem), nameof(ClientBootstrapSystem.UpdateConnectRequest),
                    prefixType: typeof(UpdateConnectHook), prefix: nameof(UpdateConnectHook.Prefix));
            case HarmonySlotPolicy.Authenticate:
                return TryPatchAuthenticate(harmony);
            default:
                return 0;
        }
    }

    private static int TryPatchMethod(
        Harmony harmony,
        Type type,
        string name,
        Type? prefixType = null,
        string? prefix = null,
        Type? postfixType = null,
        string? postfix = null)
    {
        try
        {
            var original = AccessTools.Method(type, name);
            if (original == null)
            {
                ProcessHost.Log?.LogWarning($"HarmonyPatches: {type.Name}.{name} was not found on the interop type.");
                return 0;
            }

            HarmonyMethod? pre = prefixType == null || prefix == null ? null : new HarmonyMethod(prefixType, prefix);
            HarmonyMethod? post = postfixType == null || postfix == null ? null : new HarmonyMethod(postfixType, postfix);
            harmony.Patch(original, prefix: pre, postfix: post);
            return 1;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: patching {type.Name}.{name} threw: {e}");
            return 0;
        }
    }

    private static int TryPatchAuthenticate(Harmony harmony)
    {
        try
        {
            var original = AccessTools.Method(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.Authenticate));
            if (original == null)
            {
                ProcessHost.Log?.LogWarning("HarmonyPatches: ServerBootstrapSystem.Authenticate was not found on the interop type.");
                return 0;
            }

            var pars = original.GetParameters();
            for (int i = 0; i < pars.Length; i++)
            {
                if (!pars[i].ParameterType.IsByRef) continue;
                var elem = pars[i].ParameterType.GetElementType();
                if (elem == null) continue;
                if (elem.IsValueType || IsIl2CppValueType(elem))
                {
                    ProcessHost.Log?.LogError(
                        "HarmonyPatches: Authenticate has by-ref " + elem.FullName + " " + pars[i].Name
                        + "; not patching (by-ref struct trap).");
                    return 0;
                }
            }

            harmony.Patch(original, prefix: new HarmonyMethod(typeof(AuthenticateHook), nameof(AuthenticateHook.Prefix)));
            return 1;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: Authenticate patch threw: {e}");
            return 0;
        }
    }

    private static bool IsIl2CppValueType(Type t)
    {
        try { return typeof(Il2CppSystem.ValueType).IsAssignableFrom(t); }
        catch { return false; }
    }

    private static int PatchSite(string site, Func<int> patch)
    {
        int c;
        try { c = patch(); }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: {site} threw: {e}");
            c = 0;
        }
        if (c > 0)
        {
            HarmonySlots.MarkPatched(site);
            return 1;
        }
        ProcessHost.Log?.LogError(HarmonySlotPolicy.VanillaSiteLog(site));
        return 0;
    }

    private static int TryPatchAll(Harmony harmony, Type hookType)
    {
        try
        {
            harmony.PatchAll(hookType);
            return 1;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: PatchAll({hookType.Name}) threw: {e}");
            return 0;
        }
    }

    private static int TryPatchUnmanaged(Harmony harmony)
    {
        try
        {
            var original = FindUnmanagedUpdate();
            if (original == null)
            {
                ProcessHost.Log?.LogError("HarmonyPatches: WorldUnmanagedImpl.UnmanagedUpdate(void*) was not found on the interop type.");
                return 0;
            }

            harmony.Patch(
                original,
                prefix: new HarmonyMethod(typeof(UnmanagedUpdateHook), nameof(UnmanagedUpdateHook.Prefix)) { priority = Priority.First },
                postfix: new HarmonyMethod(typeof(UnmanagedUpdateHook), nameof(UnmanagedUpdateHook.Postfix)));
            return 1;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: UnmanagedUpdate patch threw: {e}");
            return 0;
        }
    }

    private static int TryPatchStart(Harmony harmony, Type type)
    {
        try
        {
            var method = AccessTools.Method(type, "Start");
            if (method == null)
            {
                ProcessHost.Log?.LogWarning($"HarmonyPatches: {type.Name}.Start was not found on the interop type.");
                return 0;
            }

            string prefixName = string.Equals(type.Name, nameof(StunProcess_MONO), StringComparison.Ordinal)
                ? nameof(StunStartHook.PrefixPath)
                : nameof(StunStartHook.PrefixFileName);
            harmony.Patch(
                method,
                prefix: new HarmonyMethod(typeof(StunStartHook), prefixName),
                finalizer: new HarmonyMethod(typeof(StunStartHook), nameof(StunStartHook.Finalizer)));
            ProcessHost.Log?.LogInfo($"HarmonyPatches: patched {type.Name}.Start ({method.GetParameters().Length} args, prefix+finalizer).");
            return 1;
        }
        catch (Exception e)
        {
            ProcessHost.Log?.LogError($"HarmonyPatches: patching {type.Name}.Start threw: {e}");
            return 0;
        }
    }

    private static MethodInfo? FindUnmanagedUpdate()
    {
        var type = typeof(WorldUnmanagedImpl);
        foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
        {
            if (method.Name != nameof(WorldUnmanagedImpl.UnmanagedUpdate)) continue;
            var args = method.GetParameters();
            if (args.Length == 1 && args[0].ParameterType == typeof(void*)) return method;
        }
        return type.GetMethod(nameof(WorldUnmanagedImpl.UnmanagedUpdate), BindingFlags.Public | BindingFlags.Static);
    }

    /// <summary>
    /// Job FLATCH1 (R44 finding 7). The full stack trace is printed once per SITE per Core
    /// generation; later throws at that site are message-only so a system throwing every frame
    /// cannot flood the log.
    ///
    /// <para>This used to be one process-lifetime <c>int</c> shared by all twenty-six call sites
    /// below. The first throw anywhere — typically a benign <c>SetLanguage</c> or connect-path
    /// throw at boot — spent the only stack trace the process would ever print, and every later
    /// throw at every other site, in every later Core generation, was message-only. The dispatchers
    /// behind these slots are Core-generation objects (<c>HarmonySlots.ClearAll</c> nulls them on
    /// every Uninstall), so a latch that outlives the generation is a latch scoped wider than the
    /// thing it describes: you cannot tell whether a throw is in Core or in a plugin hook without
    /// restarting the process.</para>
    ///
    /// <para><b>Re-arm boundary: the Core generation.</b> <see cref="ClearAfterSnapshots"/> is
    /// already called by <c>CoreLoader</c> at both Core-unload points, so the table is cleared
    /// exactly when the dispatchers it describes are dropped — the same treatment
    /// <c>ReceiveDetours._emptyDropLogged</c> gets on every dispatcher bind. Keys are the site
    /// name strings from <see cref="HarmonySlotPolicy"/>; no Core object is referenced, so the
    /// table cannot pin a generation.</para>
    /// </summary>
    private static void LogThrow(string site, Exception e)
    {
        bool first;
        lock (ThrowLogGate) first = ThrowLogged.Add(site ?? "");
        ProcessHost.Log?.LogError(HarmonySlotPolicy.SlotThrowLog(site, first ? e.ToString() : e.Message));
    }

    // Prefix snapshots the postfix delegate so Uninstall/ClearAll nested inside this
    // Update (ProcessHost.Tick is a BootstrapSystem postfix) cannot drop After for
    // the matching call. Depth grows if a managed Update nests another.
    //
    // Job FHOOK1: these were bare arrays plus a depth int, and Harmony skips the postfix when the
    // patched original throws - so a throwing system pushed a hand-off that nothing popped, and
    // only a Core unload (ClearAfterSnapshots) ever emptied them. AfterCallStack releases a
    // stranded entry on the rendered-frame boundary instead. See that file for the argument.
    //
    // Job FHOOK2: and the pairing is by token, not by position. Push mints one, __state carries it
    // to the postfix, Release takes it back - so a caller that catches the inner system's
    // exception releases its own hand-off instead of the strand sitting on top of it.
    private static readonly AfterCallStack<AfterCall> _managedAfter = new(16);
    private static readonly AfterCallStack<AfterCall> _unmanagedAfter = new(16);
    private static int _managedStrandLogged;
    private static int _unmanagedStrandLogged;

    // Job FHOOK3: the entry carries CORE's call token, not a "Core has After handlers" bit. Zero
    // still means "Core pushed nothing for this call", so the postfix's one-compare test is
    // unchanged; a non-zero value additionally tells Core WHICH of its frames this postfix is
    // for, which is what stops a catching outer system from dispatching an inner one's handlers.
    private struct AfterCall
    {
        public long CoreToken;
        public HarmonySlots.ManagedUpdatePostfix? Managed;
        public HarmonySlots.UnmanagedUpdatePostfix? Unmanaged;
    }

    /// <summary>
    /// Drop leftover prefix snapshots. A nested <c>SystemBase.Update</c> that was on
    /// the stack at UnloadAlc can leave a Core postfix delegate in this array and pin
    /// generation N across the collect poll. Idempotent; next Prefix pushes fresh.
    ///
    /// <para>Job FLATCH1: also the re-arm point for <see cref="LogThrow"/>'s per-site stack-trace
    /// latch. Both are per-Core-generation state about dispatchers that are about to be nulled,
    /// and CoreLoader already calls this at both unload points, so they re-arm together.</para>
    ///
    /// <para>Job FHOOK1: and the re-arm point for the strand report, on the same reasoning. This
    /// is no longer the <em>only</em> thing that empties the two stacks — <c>AfterCallStack.Push</c>
    /// releases strands every frame now — but it is still what guarantees no entry crosses an
    /// unload holding generation N's postfix delegate, which is why it clears live frames too.
    /// </para>
    ///
    /// <para>Job FHOOK2: <c>Clear</c> deliberately does <b>not</b> reset the token counter. A
    /// postfix still in flight across this call holds a token for a frame that is now gone; the
    /// counter never repeating is what makes it release nothing instead of releasing whatever
    /// generation N+1 pushed into the same slot.</para>
    /// </summary>
    internal static void ClearAfterSnapshots()
    {
        _managedAfter.Clear();
        _unmanagedAfter.Clear();
        System.Threading.Interlocked.Exchange(ref _managedStrandLogged, 0);
        System.Threading.Interlocked.Exchange(ref _unmanagedStrandLogged, 0);
        lock (ThrowLogGate) ThrowLogged.Clear();
    }

    /// <summary>
    /// Job FHOOK1. Push one prefix's hand-off, stamped with Bootstrap's own frame clock so the
    /// stack can release what a throwing Update stranded. <c>ProcessHost.FrameCount</c> is a
    /// static <c>int</c> field, not a Unity call: this runs on every patched
    /// <c>SystemBase.Update</c>.
    ///
    /// <para>Job FHOOK2: returns the release token the matching postfix carries in
    /// <c>__state</c>. Zero is never minted, so the postfix's "did my prefix push anything"
    /// question is the same one-compare test the old <c>bool</c> answered.</para>
    /// </summary>
    private static long PushManaged(long coreToken, HarmonySlots.ManagedUpdatePostfix? postfix)
    {
        long token = _managedAfter.Push(new AfterCall { CoreToken = coreToken, Managed = postfix }, ProcessHost.FrameCount);
        ReportStrands(_managedAfter, ref _managedStrandLogged, HarmonySlotPolicy.SystemBaseUpdate);
        return token;
    }

    /// <summary>
    /// Job FHOOK2. Take back the hand-off <paramref name="token"/> names — this call's own, not
    /// whatever is on top of the stack. Reporting runs here too because with the token most
    /// strands are now released by their enclosing caller rather than by the frame sweep, and a
    /// strand that nothing ever logged is a strand nobody knows about.
    /// </summary>
    private static AfterCall ReleaseManaged(long token)
    {
        var frame = _managedAfter.Release(token);
        ReportStrands(_managedAfter, ref _managedStrandLogged, HarmonySlotPolicy.SystemBaseUpdate);
        return frame;
    }

    private static long PushUnmanaged(long coreToken, HarmonySlots.UnmanagedUpdatePostfix? postfix)
    {
        long token = _unmanagedAfter.Push(new AfterCall { CoreToken = coreToken, Unmanaged = postfix }, ProcessHost.FrameCount);
        ReportStrands(_unmanagedAfter, ref _unmanagedStrandLogged, HarmonySlotPolicy.UnmanagedUpdate);
        return token;
    }

    private static AfterCall ReleaseUnmanaged(long token)
    {
        var frame = _unmanagedAfter.Release(token);
        ReportStrands(_unmanagedAfter, ref _unmanagedStrandLogged, HarmonySlotPolicy.UnmanagedUpdate);
        return frame;
    }

    /// <summary>
    /// One line per site per Core generation. A strand means a patched Update threw; the throw is
    /// reported by whoever caught it, so this only says that the hand-off was released by the
    /// sweep rather than by its own postfix, and a system throwing every frame cannot flood.
    /// </summary>
    private static void ReportStrands(AfterCallStack<AfterCall> stack, ref int logged, string site)
    {
        long total = stack.AbandonedTotal;
        if (total == 0 || logged != 0) return;
        if (System.Threading.Interlocked.Exchange(ref logged, 1) != 0) return;
        ProcessHost.Log?.LogWarning(AfterCallStrand.Report(site, total));
    }

    /// <summary>
    /// Job FHOOK2. <c>__state</c> carries an <see cref="AfterCallStack{T}"/> release token instead
    /// of a "did I push" bit, so the postfix releases the hand-off <em>this</em> call pushed. The
    /// bit is not lost: zero is never a minted token, so <c>__state == 0</c> still means the
    /// prefix pushed nothing.
    ///
    /// <para><b>The Harmony ABI this widens, exactly.</b> <c>__state</c> is a local in the patched
    /// method, and HarmonyX declares one per <em>patch declaring type</em>: in
    /// <c>HarmonyManipulator</c> the local is registered as
    /// <c>variables[patch.DeclaringType.FullName]</c>, typed from the first <c>__state</c>
    /// parameter that type declares, and read back by the same key. So the type is shared by
    /// every prefix/postfix/finalizer inside <em>one</em> patch class and by nothing else — not by
    /// other patches on <c>SystemBase.Update</c>, not by this file's other hook classes, not by
    /// another plugin's patches on the same method. Widening here therefore reaches exactly the
    /// four methods below, and their agreement is not checked for us: a prefix and postfix in the
    /// same class that disagreed would emit a <c>Ldloc</c> of the wrong type rather than throw at
    /// patch time. <c>StunStartHook</c> (this file) and Core's <c>HostedServerLauncher</c> and
    /// <c>HarmonyFallback</c> keep their own <c>bool __state</c>, in their own declaring types.
    /// Nothing outside a patch body can read <c>__state</c> at all — it never leaves the method.
    /// </para>
    ///
    /// <para><b>Job FHOOK3 carried the identity one hop further, and that hop is not free.</b>
    /// Core's own <c>HookCallStack</c> used to be popped positionally for the same reason FHOOK2
    /// gave: the <c>ManagedUpdatePostfix</c> slot delegate carried one <c>bool</c>. The four update
    /// slot delegates in <see cref="HarmonySlots"/> now carry a <c>long callToken</c> instead, so
    /// the frame this postfix releases names the Core frame it is for. Unlike <c>__state</c>, that
    /// <em>is</em> observable outside this file — it is the cross-ALC contract joined by
    /// <c>Delegate.CreateDelegate</c> in <c>HarmonySlots.Bind</c>, which throws on skew — so it
    /// carries a protocol bump (<c>CoreLayout.ProtocolVersion</c> 4). A half-deployed pair is then
    /// refused at load with both numbers in the log rather than binding a skewed delegate and
    /// taking the site Inert. Core's <c>SystemHooks.PrefixManaged</c> is <b>also</b> a Harmony
    /// patch on the no-Bootstrap <c>HarmonyFallback</c> path, and its <c>__state</c> widened with
    /// it — a third declaring type, a third independent local.</para>
    /// </summary>
    private static class ManagedUpdateHook
    {
        [HarmonyPatch(typeof(SystemBase), nameof(SystemBase.Update))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool Prefix(SystemBase __instance, ref long __state)
        {
            __state = 0;
            if (!ControlFormService.BeforeSystem(__instance)) return false;
            var postfix = HarmonySlots.ManagedPostfix;
            var slot = HarmonySlots.ManagedPrefix;
            long coreToken = 0;
            bool run = true;
            if (slot != null)
            {
                try { run = slot(__instance, ref coreToken); }
                catch (Exception e)
                {
                    LogThrow(HarmonySlotPolicy.SystemBaseUpdatePrefix, e);
                    run = true;
                    coreToken = 0;
                }
            }
            __state = PushManaged(coreToken, postfix);
            return run;
        }

        [HarmonyPatch(typeof(SystemBase), nameof(SystemBase.Update))]
        [HarmonyPostfix]
        private static void Postfix(long __state)
        {
            if (__state == 0) return;
            var frame = ReleaseManaged(__state);
            if (frame.CoreToken == 0) return;
            var slot = frame.Managed;
            if (slot == null) return;
            try { slot(frame.CoreToken); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.SystemBaseUpdatePostfix, e); }
        }
    }

    /// <summary>
    /// The unmanaged twin, on the same token ABI and for the same reason. See
    /// <see cref="ManagedUpdateHook"/> for what widening <c>__state</c> does and does not reach;
    /// this is a second declaring type, so it gets a second, independent local.
    /// </summary>
    private static unsafe class UnmanagedUpdateHook
    {
        public static bool Prefix(void* pSystemState, ref long __state)
        {
            __state = 0;
            var postfix = HarmonySlots.UnmanagedPostfix;
            var slot = HarmonySlots.UnmanagedPrefix;
            long coreToken = 0;
            bool run = true;
            if (slot != null)
            {
                try { run = slot((IntPtr)pSystemState, ref coreToken); }
                catch (Exception e)
                {
                    LogThrow(HarmonySlotPolicy.UnmanagedUpdatePrefix, e);
                    run = true;
                    coreToken = 0;
                }
            }
            __state = PushUnmanaged(coreToken, postfix);
            return run;
        }

        public static void Postfix(long __state)
        {
            if (__state == 0) return;
            var frame = ReleaseUnmanaged(__state);
            if (frame.CoreToken == 0) return;
            var slot = frame.Unmanaged;
            if (slot == null) return;
            try { slot(frame.CoreToken); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.UnmanagedUpdatePostfix, e); }
        }
    }

    [HarmonyPriority(300)]
    private static class ChatHook
    {
        [HarmonyPatch(typeof(ChatMessageSystem), nameof(ChatMessageSystem.OnUpdate))]
        [HarmonyPrefix]
        private static void Prefix(ChatMessageSystem __instance)
        {
            var slot = HarmonySlots.Chat;
            if (slot == null) return;
            try { slot(__instance); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.ChatOnUpdate, e); }
        }
    }

    private static class UserConnectedHook
    {
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserConnected))]
        [HarmonyPostfix]
        private static void Connected(ServerBootstrapSystem __instance, NetConnectionId netConnectionId)
        {
            var slot = HarmonySlots.Connected;
            if (slot == null) return;
            try { slot(__instance, netConnectionId); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.UserConnected, e); }
        }
    }

    private static class UserDisconnectedHook
    {
        [HarmonyPatch(typeof(ServerBootstrapSystem), nameof(ServerBootstrapSystem.OnUserDisconnected))]
        [HarmonyPrefix]
        private static void Disconnected(ServerBootstrapSystem __instance, NetConnectionId netConnectionId, ConnectionStatusChangeReason connectionStatusReason, string extraData)
        {
            // Bootstrap recovery precedes native disconnect and any collectible subscriber.
            ControlFormService.BeforeDisconnect(__instance, netConnectionId);
            var slot = HarmonySlots.Disconnected;
            if (slot == null) return;
            string reason;
            try { reason = connectionStatusReason.ToString(); }
            catch { reason = ""; }
            try { slot(__instance, netConnectionId, reason, extraData); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.UserDisconnected, e); }
        }
    }

    private static class DeserializeHook
    {
        [HarmonyPatch(typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.DeserializeLoadFile))]
        [HarmonyPrefix]
        private static void BeforeDeserialize()
        {
            // Before any slot, so a handed-off despawn from the world being replaced is never resumed.
            ProcessHost.Despawns.NoteWorldLoad();
            var slot = HarmonySlots.DeserializePrefix;
            if (slot == null) return;
            try { slot(); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.DeserializeLoadFilePrefix, e); }
        }

        [HarmonyPatch(typeof(LoadPersistenceSystemV2), nameof(LoadPersistenceSystemV2.DeserializeLoadFile))]
        [HarmonyPostfix]
        private static void AfterDeserialize()
        {
            var slot = HarmonySlots.DeserializePostfix;
            if (slot == null) return;
            try { slot(); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.DeserializeLoadFilePostfix, e); }
        }
    }

    private static class FormLoadRequestHook
    {
        [HarmonyPatch(typeof(PersistenceV2Test), nameof(PersistenceV2Test.TriggerNewLoad))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool BeforeRequest(World world)
        {
            try { return ControlFormService.BeforeLoad(world); }
            catch (Exception e) { LogThrow("control.form.load.request", e); return false; }
        }
    }

    private static class FormLoadResetHook
    {
        // Reset may be called independently of the request helper. Refuse before any native
        // reset work, while all recorded entity identities still refer to the original load.
        [HarmonyPatch(typeof(LoadPersistenceSystemV2), "ResetLoadedPersistenceForTest")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool BeforeReset(LoadPersistenceSystemV2 __instance)
        {
            try { return ControlFormService.BeforeLoad(__instance.World); }
            catch (Exception e) { LogThrow("control.form.load.reset", e); return false; }
        }
    }
    private static class FormLoadUpdateHook
    {
        // Native order is documented in the internal research notes: this contains
        // Initialize, DeserializeHeader, DeserializeWorldData and all instance creation.
        // Deferring the complete tick preserves its outputs and prevents partial deserialization.
        [HarmonyPatch(typeof(LoadPersistenceSystemV2), "OnUpdate")]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool BeforeUpdate(LoadPersistenceSystemV2 __instance)
        {
            try { return ControlFormService.BeforeLoad(__instance.World); }
            catch (Exception e) { LogThrow("control.form.load.update", e); return false; }
        }
    }
    private static class FormSerializeHook
    {
        // The request-side TriggerSave only queues an event. Guard actual serialization instead.
        [HarmonyPatch(typeof(SerializePersistenceSystemV2), nameof(SerializePersistenceSystemV2.TriggerSave))]
        [HarmonyPrefix]
        [HarmonyPriority(Priority.First)]
        private static bool BeforeSerialize(SerializePersistenceSystemV2 __instance)
        {
            try { return ControlFormService.BeforeSave(__instance.World); }
            catch (Exception e)
            {
                ProcessHost.Log?.LogError("FORMS serialize guard refused: " + e.Message);
                return false;
            }
        }
    }

    private static class TriggerSaveHook
    {
        [HarmonyPatch(typeof(TriggerPersistenceSaveSystem), nameof(TriggerPersistenceSaveSystem.TriggerSave))]
        [HarmonyPostfix]
        private static void AfterTriggerSave(bool __result)
        {
            // WORLDRESTORE2: the first save after a restore lets the next start remove the replaced world.
            if (Plugin.IsServerProcess)
            {
                try { WorldRestoreAtStart.NoteSave(__result); }
                catch { /* never break TriggerSave */ }
            }

            var slot = HarmonySlots.TriggerSaveSlot;
            if (slot == null) return;
            try { slot(__result); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.TriggerSave, e); }
        }
    }

    private static class BeginCreateHook
    {
        [HarmonyPatch(typeof(ServerWorldManager), nameof(ServerWorldManager.BeginCreateServerWorld))]
        [HarmonyPostfix]
        private static void AfterBeginCreate(ServerRuntimeSettings saveConfiguration)
        {
            string? dir = null;
            string? name = null;
            try { dir = saveConfiguration.SaveDirectoryPath.ToString(); }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonyPatches: SaveDirectoryPath: {e.Message}"); }
            try
            {
                var optional = saveConfiguration.SaveToLoad;
                if (optional.HasValue) name = optional.Value.ToString();
            }
            catch (Exception e) { ProcessHost.Log?.LogWarning($"HarmonyPatches: SaveToLoad: {e.Message}"); }
            // Bootstrap-side capture so the save-safety guard can name the save with no Core loaded.
            try { SaveSafety.NoteSave(dir, name); }
            catch { /* never break BeginCreateServerWorld */ }
            // WORLDRESTORE1 phase 0: when the game chose its save, relative to Plugin.Load (where a restore is applied).
            try { WorldRestoreAtStart.NoteBeginCreate(dir, name, ProcessHost.Log == null ? null : ProcessHost.Log.LogInfo); }
            catch { /* never break BeginCreateServerWorld */ }
            var slot = HarmonySlots.BeginCreate;
            if (slot == null) return;
            try { slot(dir, name); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.BeginCreateServerWorld, e); }
        }
    }

    /// <summary>
    /// Save-safety load guard prefix (job FSAVE). Signature matches
    /// <c>private void CopyDataFromPrefabs(DeserializationJobData jobData)</c>
    /// in the server metadata; Harmony matches <c>jobData</c> by name.
    /// The body never throws into the loader.
    /// </summary>
    private static class SaveSafetyHook
    {
        public static void Prefix(LoadPersistenceSystemV2 __instance, DeserializationJobData jobData)
        {
            try { SaveSafety.BeforeCopyDataFromPrefabs(__instance, jobData); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.CopyDataFromPrefabs, e); }
        }

        /// <summary>
        /// The irreversible half. Runs only when the original returned normally, which is exactly
        /// the condition under which destroying anything is safe. Takes the same arguments as the
        /// prefix so it can repair <c>BuffBuffer</c> rows on the deserialized holders.
        /// </summary>
        public static void Postfix(LoadPersistenceSystemV2 __instance, DeserializationJobData jobData)
        {
            try { SaveSafety.AfterCopyDataFromPrefabs(__instance, jobData); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.CopyDataFromPrefabs, e); }
        }
    }

    private static class StunStartHook
    {
        // Full signatures so Harmony can match IL2CPP by position as well as by name.
        // StunProcess_MONO names the first parameter path.
        public static bool PrefixFileName(string fileName, string arguments, string dir, bool hidden, bool redirectStandardInput, ref bool __state) =>
            PrefixCore(fileName, arguments, dir, hidden, redirectStandardInput, ref __state);

        public static bool PrefixPath(string path, string arguments, string dir, bool hidden, bool redirectStandardInput, ref bool __state) =>
            PrefixCore(path, arguments, dir, hidden, redirectStandardInput, ref __state);

        private static bool PrefixCore(string? fileName, string arguments, string dir, bool hidden, bool redirectStandardInput, ref bool __state)
        {
            _ = hidden;
            _ = redirectStandardInput;
            ProcessHost.NoteHostedSpawn(fileName, arguments);
            __state = false;
            var slot = HarmonySlots.StunPrefix;
            if (slot == null) return true;
            try
            {
                bool run = slot(fileName, dir, out bool entered);
                __state = entered;
                return run;
            }
            catch (Exception e)
            {
                LogThrow(HarmonySlotPolicy.StunProcessStartPrefix, e);
                return true;
            }
        }

        public static void Finalizer(StunProcess __instance, Exception? __exception, bool __state)
        {
            // Job HOSTPROBE: hand the hosted child's PID to the tracker so it never has to scan.
            // Only after a Start the prefix recorded as hosted, and only when Start did not throw.
            // A hint: the tracker still matches path + argv before it adopts the process.
            if (ProcessHost.TakeHostedSpawnAwaitingPid() && __exception == null && __instance != null)
            {
                try
                {
                    uint pid = __instance.ProcessId;
                    if (pid > 0 && pid <= int.MaxValue) ProcessHost.NoteHostedSpawnPid((int)pid);
                }
                catch { /* no hint: the background scan identifies the child */ }
            }

            var slot = HarmonySlots.StunFinalizer;
            if (slot == null) return;
            try { slot(__state); }
            catch (Exception e)
            {
                try { LogThrow(HarmonySlotPolicy.StunProcessStartFinalizer, e); }
                catch { /* never replace StunProcess.Start's exception */ }
            }
        }
    }

    private static class SetLanguageHook
    {
        [HarmonyPatch(typeof(GameLocalization), nameof(GameLocalization.SetLanguage))]
        [HarmonyPostfix]
        private static void Postfix(string language)
        {
            var slot = HarmonySlots.Language;
            if (slot == null) return;
            try { slot(language); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.SetLanguage, e); }
        }
    }

    // Plugin / extra-instance sites. Empty slot = vanilla. Bodies stay in Bootstrap so
    // collectible Dev never contributes a patch MethodInfo (evidence 66).

    private static class LaunchGameHook
    {
        public static void Prefix(GameConnect launchData)
        {
            try
            {
                bool shouldHost = launchData.ShouldHost;
                string? save = null;
                try { save = launchData.ServerSaveName.ToString(); }
                catch { save = null; }
                ProcessHost.NoteDedicatedJoin(shouldHost, save);
                // SERVERINST5: the port this client is joining. Verified live
                // (research hosted-game-launch-op.md:258-261) to be the game port.
                // Read field by field, each guarded: ConnectAddress carries a
                // FixedString and must never be read through ToString() on the wrapper.
                int port = 0;
                string host = "";
                string protocol = "";
                try
                {
                    var connect = launchData.ConnectAddress;
                    port = connect.Port;
                    try { host = connect.Address.ToString() ?? ""; }
                    catch { host = ""; }
                    try { protocol = connect.Protocol.ToString() ?? ""; }
                    catch { protocol = ""; }
                }
                catch
                {
                    port = 0;
                }
                ProcessHost.NoteConnect(protocol, host, port);
            }
            catch
            {
                // Prefix must not throw into LaunchGame.
            }
            var slot = HarmonySlots.LaunchGameSlot;
            if (slot == null) return;
            try { slot(launchData); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.LaunchGame, e); }
        }
    }

    private static class GameBootstrapStartHook
    {
        public static void Prefix(GameBootstrap __instance)
        {
            var slot = HarmonySlots.BootstrapStart;
            if (slot == null) return;
            try { slot(__instance); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.GameBootstrapStart, e); }
        }
    }

    private static class GameBootstrapAwakeHook
    {
        public static void Prefix(GameBootstrap __instance)
        {
            var slot = HarmonySlots.BootstrapAwake;
            if (slot == null) return;
            try { slot(__instance); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.GameBootstrapAwake, e); }
        }
    }

    private static class SendPacketToUserHook
    {
        public static void Postfix(NativeArray<byte> data, int lengthBits, int userIndex, bool reliable, bool isConnectedUserIndex, StunNetSendResult __result)
        {
            var slot = HarmonySlots.SendToUser;
            if (slot == null) return;
            string result;
            try { result = __result.ToString(); }
            catch { result = ""; }
            try { slot(data, lengthBits, userIndex, reliable, isConnectedUserIndex, result); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.SendPacketToUser, e); }
        }
    }

    private static class AuthTicketHook
    {
        // serverIdentity omitted: SteamNetworkingIdentity is an Il2CppSystem.ValueType
        // wrapper (evidence §23 trampoline).
        public static bool Prefix(bool isLAN, World clientWorld, ConnectAddress connectAddress, ConnectAddress fallbackConnectAddress, string password)
        {
            var slot = HarmonySlots.AuthTicket;
            if (slot == null) return true;
            try { return slot(isLAN, clientWorld, connectAddress, fallbackConnectAddress, password); }
            catch (Exception e)
            {
                LogThrow(HarmonySlotPolicy.GetAuthSessionTicket, e);
                return true;
            }
        }
    }

    private static class SteamAddressHook
    {
        public static void Postfix(string address, ushort port, ref ConnectAddress __result)
        {
            var slot = HarmonySlots.SteamAddress;
            if (slot == null) return;
            try { slot(address, port, ref __result); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.CreateSteamIPv4, e); }
        }
    }

    private static class ConnectHook
    {
        public static void Prefix()
        {
            var slot = HarmonySlots.ConnectPre;
            if (slot == null) return;
            try { slot(); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.ClientBootstrapConnectPrefix, e); }
        }

        public static void Postfix(ClientBootstrapSystem __instance)
        {
            var slot = HarmonySlots.ConnectPost;
            if (slot == null) return;
            try { slot(__instance); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.ClientBootstrapConnectPostfix, e); }
        }
    }

    private static class UpdateConnectHook
    {
        public static void Prefix(ClientBootstrapSystem __instance)
        {
            var slot = HarmonySlots.UpdateConnect;
            if (slot == null) return;
            try { slot(__instance); }
            catch (Exception e) { LogThrow(HarmonySlotPolicy.UpdateConnectRequest, e); }
        }
    }

    private static class AuthenticateHook
    {
        // hailData omitted: HailMessageStruct_Client is an Il2CppSystem.ValueType wrapper
        // (R12 finding 12 / evidence §23). Harmony matches by name.
        public static bool Prefix(
            ServerBootstrapSystem __instance,
            NetConnectionId netConnectionId,
            ulong platformUserId,
            string eosId,
            int usersTaken,
            int maxConnectedUsers,
            bool connectedAsAdmin)
        {
            var slot = HarmonySlots.AuthenticateSlot;
            if (slot == null) return true;
            try
            {
                return slot(__instance, netConnectionId, platformUserId, eosId, usersTaken, maxConnectedUsers, connectedAsAdmin);
            }
            catch (Exception e)
            {
                LogThrow(HarmonySlotPolicy.Authenticate, e);
                return true;
            }
        }
    }
}
