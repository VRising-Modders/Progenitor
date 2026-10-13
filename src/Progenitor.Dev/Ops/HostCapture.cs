using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using ProjectM;
using Progenitor;
using ProjectM.UI;
using Stunlock.Core;
using Stunlock.Network;
using UnityEngine;
using UnityObject = UnityEngine.Object;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Client Dev op <c>host_capture</c>: Bootstrap-owned prefixes on
/// <see cref="GameHelper.LaunchGame"/> and <see cref="GameBootstrap.Start"/> that dump
/// <see cref="GameConnect"/>, <see cref="SettingsManager"/> host/game settings, and the
/// <see cref="GameBootstrap"/> instance at the moment of the hosted-launch NRE.
/// Three live <c>host</c> ops died in <c>GameBootstrap.Start</c> (evidence §§7, 13, 16);
/// guessing from stubs is exhausted. The real Private Game click is the remaining ground
/// truth — capture it, then diff against the bridge <c>host</c> op.
/// Installed by default on the client when the Dev plugin loads. Cost is a log dump per
/// call; both methods run once per launch.
/// </summary>
internal static class HostCapture
{
    private const string LogPrefix = "host_capture:";
    private const int StringCap = 256;
    private const BindingFlags DeclaredInstance =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly;
    private const BindingFlags DeclaredStatic =
        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly;

    private static readonly object Gate = new();
    private static readonly string CapturePath =
        Path.Combine(Path.GetTempPath(), "progenitor-host-capture.txt");

    private static bool _bound;
    private static bool _startPatchable;
    private static bool _startPatched;
    private static bool _awakePatched;
    private static bool _launchGamePatched;
    private static string? _startPatchError;
    private static string? _awakePatchError;
    private static string? _launchGamePatchError;
    private static int _dumpSeq;

    /// <summary>
    /// Install the prefixes. Client only; a server process is a no-op. Safe to call twice.
    /// <see cref="GameBootstrap.Start"/> has no by-ref struct args (public C# wrapper over
    /// native <c>Start_Private_Void_0</c>), so HarmonyX can patch the IL2CPP method pointer
    /// Unity invokes for the message. If that patch throws at install, fall back to
    /// <see cref="GameBootstrap.Awake"/> (same shape). Live confirmation is the first
    /// Private Game click: a <c>GameBootstrap.Start prefix</c> line in the capture file.
    /// </summary>
    internal static void Install()
    {
        lock (Gate)
        {
            if (Progenitor.Plugin.IsServerProcess)
            {
                Line("skip install: server process");
                return;
            }

            if (_bound)
            {
                Line($"already installed startPatchable={_startPatchable} startPatched={_startPatched} awakePatched={_awakePatched} launchGamePatched={_launchGamePatched}");
                return;
            }

            try
            {
                TryBindLaunchGame();
                TryBindStart();
                _bound = true;
                Line($"installed launchGame={_launchGamePatched} startPatchable={_startPatchable} startPatched={_startPatched} awakePatched={_awakePatched} file={CapturePath}");
            }
            catch (Exception e)
            {
                ClearBinds();
                _bound = false;
                _launchGamePatched = false;
                _startPatched = false;
                _awakePatched = false;
                Line($"install threw: {e}");
                throw;
            }
        }
    }

    /// <summary>Remove the prefixes. Called from the Dev plugin Unload and from <c>enable:false</c>.</summary>
    internal static void Shutdown()
    {
        lock (Gate)
        {
            if (!_bound)
            {
                _launchGamePatched = false;
                _startPatched = false;
                _awakePatched = false;
                return;
            }

            ClearBinds();
            _bound = false;
            _launchGamePatched = false;
            _startPatched = false;
            _awakePatched = false;
            Line("uninstalled");
        }
    }

    /// <summary>
    /// Bridge op. <c>enable</c> installs or removes the prefixes at runtime.
    /// <c>ops</c> / <c>action</c> = <c>dump</c> writes the settings + GameBootstrap snapshot
    /// now (menu or loading screen). Empty args dump. Client, main thread.
    /// </summary>
    internal static object Run(JsonElement args)
    {
        try
        {
            if (Progenitor.Plugin.IsServerProcess)
                throw new InvalidOperationException("host_capture runs on the client side only");

            bool? enable = GetOptionalBool(args, "enable");
            var ops = GetString(args, "ops") ?? GetString(args, "action");
            bool dump = string.Equals(ops, "dump", StringComparison.OrdinalIgnoreCase)
                        || (enable == null && string.IsNullOrEmpty(ops));

            if (!string.IsNullOrEmpty(ops)
                && !string.Equals(ops, "dump", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException($"unknown host_capture ops '{ops}' (expected dump)");

            if (enable == true) Install();
            else if (enable == false) Shutdown();

            int lines = 0;
            if (dump)
                lines = DumpNow("bridge dump");

            return Status(dumped: dump, dumpLines: lines, enable);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"{LogPrefix} op threw: {e}");
            throw;
        }
    }

    // ---- slots ------------------------------------------------------------------------------

    private static void TryBindLaunchGame()
    {
        try
        {
            if (!HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.LaunchGame, new Action<GameConnect>(LaunchGameHook.Prefix)))
            {
                _launchGamePatchError = "GameHelper.LaunchGame slot bind failed (Bootstrap trampoline missing)";
                Line(_launchGamePatchError);
                _launchGamePatched = false;
                return;
            }
            _launchGamePatched = true;
            _launchGamePatchError = null;
        }
        catch (Exception e)
        {
            _launchGamePatched = false;
            _launchGamePatchError = e.Message;
            Line($"GameHelper.LaunchGame slot bind failed: {e}");
        }
    }

    /// <summary>
    /// <see cref="GameBootstrap.Start"/> is <c>public void Start()</c> on the interop wrapper
    /// (native name <c>Start_Private_Void_0</c>). Bootstrap owns the prefix; this method
    /// only fills the slot. If the Start trampoline is missing, fall back to Awake.
    /// </summary>
    private static void TryBindStart()
    {
        try
        {
            if (HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.GameBootstrapStart, new Action<GameBootstrap>(StartHook.Prefix)))
            {
                _startPatchable = true;
                _startPatched = true;
                _startPatchError = null;
                return;
            }

            _startPatchable = false;
            _startPatched = false;
            _startPatchError = "GameBootstrap.Start slot bind failed (Bootstrap trampoline missing)";
            Line(_startPatchError + "; falling back to Awake");
            TryBindAwake();
        }
        catch (Exception e)
        {
            _startPatchable = false;
            _startPatched = false;
            _startPatchError = e.Message;
            Line($"GameBootstrap.Start slot bind failed: {e.Message}; falling back to Awake");
            TryBindAwake();
        }
    }

    private static void TryBindAwake()
    {
        try
        {
            if (!HarmonySlotHandoff.TryBindPlugin(HarmonySlotHandoff.GameBootstrapAwake, new Action<GameBootstrap>(AwakeHook.Prefix)))
            {
                _awakePatchError = "GameBootstrap.Awake slot bind failed (Bootstrap trampoline missing)";
                Line(_awakePatchError);
                _awakePatched = false;
                return;
            }
            _awakePatched = true;
            _awakePatchError = null;
            Line("GameBootstrap.Awake prefix bound (Start was not patchable)");
        }
        catch (Exception e)
        {
            _awakePatched = false;
            _awakePatchError = e.Message;
            Line($"GameBootstrap.Awake slot bind failed: {e}");
        }
    }

    private static void ClearBinds()
    {
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.LaunchGame); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.GameBootstrapStart); } catch { /* best-effort */ }
        try { HarmonySlotHandoff.ClearPlugin(HarmonySlotHandoff.GameBootstrapAwake); } catch { /* best-effort */ }
    }

    private static class LaunchGameHook
    {
        // Original takes GameConnect by value (CallerCount 21). Harmony prefix matching that
        // shape is IL2CPP-safe; the by-ref-struct trap is for original methods with ref structs.
        public static void Prefix(GameConnect launchData)
        {
            try
            {
                DumpLaunchGame(launchData);
            }
            catch (Exception e)
            {
                Line($"LaunchGame prefix threw: {e}");
            }
        }
    }

    private static class StartHook
    {
        public static void Prefix(GameBootstrap __instance)
        {
            try
            {
                DumpBootstrap("GameBootstrap.Start prefix", __instance);
            }
            catch (Exception e)
            {
                Line($"Start prefix threw: {e}");
            }
        }
    }

    private static class AwakeHook
    {
        public static void Prefix(GameBootstrap __instance)
        {
            try
            {
                DumpBootstrap("GameBootstrap.Awake prefix (Start fallback)", __instance);
            }
            catch (Exception e)
            {
                Line($"Awake prefix threw: {e}");
            }
        }
    }

    // ---- dumps --------------------------------------------------------------------------------

    private static void DumpLaunchGame(GameConnect connect)
    {
        Header("GameHelper.LaunchGame prefix");
        DumpGameConnect(connect);
        DumpSettingsSnapshot();
        DumpNamedStatics();
        DumpSceneObjects(includeBootstrap: true);
        Line("end LaunchGame prefix");
    }

    private static void DumpBootstrap(string reason, GameBootstrap? instance)
    {
        Header(reason);
        DumpSettingsSnapshot();
        DumpNamedStatics();
        if (instance == null)
            Line("GameBootstrap __instance=null");
        else
            DumpMembers(instance, "GameBootstrap");
        DumpSceneObjects(includeBootstrap: false);
        Line("end " + reason);
    }

    private static int DumpNow(string reason)
    {
        int before;
        lock (Gate) { before = _dumpSeq; }
        Header(reason);
        DumpSettingsSnapshot();
        DumpNamedStatics();
        DumpSceneObjects(includeBootstrap: true);
        Line("end " + reason);
        int after;
        lock (Gate) { after = _dumpSeq; }
        return Math.Max(0, after - before);
    }

    private static void DumpGameConnect(GameConnect connect)
    {
        // Boxed copy for reflection. Do not pass GameConnect as a generic argument (trap 3).
        object boxed = connect;
        Line("GameConnect (LaunchGame argument):");
        DumpStructFields(boxed, "GameConnect", nestedStructDepth: 1);
    }

    private static void DumpSettingsSnapshot()
    {
        Line("SettingsManager flags:");
        DumpStaticMembers(typeof(SettingsManager), "SettingsManager");

        ServerHostSettings? host = null;
        try { host = SettingsManager.ServerHostSettings; }
        catch (Exception e) { Line($"SettingsManager.ServerHostSettings threw: {Brief(e)}"); }
        Line($"SettingsManager.ServerHostSettings={(host == null ? "null" : "non-null")}");
        if (host != null)
            DumpMembers(host, "ServerHostSettings");

        ServerGameSettings? game = null;
        try { game = SettingsManager.ServerGameSettings; }
        catch (Exception e) { Line($"SettingsManager.ServerGameSettings threw: {Brief(e)}"); }
        Line($"SettingsManager.ServerGameSettings={(game == null ? "null" : "non-null")}");
        if (game != null)
            DumpMembers(game, "ServerGameSettings");
    }

    /// <summary>
    /// Holders named in the hosted-game launch research: there is no client
    /// <c>CurrentSaveName</c> static (verified absent). Save name lives on
    /// <c>ServerHostSettings.SaveName</c> and <c>GameConnect.ServerSaveName</c>. Difficulty
    /// preset is <c>ServerHostSettings.GameDifficultyPreset</c>. <c>ConnectingView._GameConnect</c>
    /// is the loading-UI copy. <c>ServerRuntimeSettings.SaveToLoad</c> is a server-side instance
    /// field, not a client static.
    /// </summary>
    private static void DumpNamedStatics()
    {
        Line("named static holders:");
        Line("CurrentSaveName=(absent from client stubs; use ServerHostSettings.SaveName)");
        TryLine("GameBootstrap.IsServerOrClientRunning", () => GameBootstrap.IsServerOrClientRunning.ToString());
        TryLine("GameBootstrap.IsServerRunning", () => GameBootstrap.IsServerRunning.ToString());
        TryLine("GameBootstrap.IsClientRunning", () => GameBootstrap.IsClientRunning.ToString());
        TryLine("StunApplication.persistentDataPath", () => StunApplication.persistentDataPath ?? "null");

        try
        {
            var host = SettingsManager.ServerHostSettings;
            Line($"saveName(ServerHostSettings.SaveName)={Quote(host?.SaveName)}");
            Line($"GameDifficultyPreset={Quote(host?.GameDifficultyPreset)}");
            Line($"GameSettingsPreset={Quote(host?.GameSettingsPreset)}");
        }
        catch (Exception e)
        {
            Line($"saveName/difficultyPreset threw: {Brief(e)}");
        }

        try
        {
            var game = SettingsManager.ServerGameSettings;
            Line($"GameDifficulty={game?.GameDifficulty.ToString() ?? "null"}");
        }
        catch (Exception e)
        {
            Line($"GameDifficulty threw: {Brief(e)}");
        }

        try
        {
            var client = SettingsManager.ClientSettings;
            Line($"SettingsManager.ClientSettings={(client == null ? "null" : "non-null")}");
            var gameClient = client?.GameClient;
            Line($"ClientSettings.GameClient={(gameClient == null ? "null" : "non-null")}");
            if (gameClient != null)
                Line($"ClientSettings.GameClient.IsPrivateGame={gameClient.IsPrivateGame}");
            var hostSettings = client?.HostSettings;
            Line($"ClientSettings.HostSettings={(hostSettings == null ? "null" : "non-null")}");
            if (hostSettings != null)
            {
                Line($"ClientSettings.HostSettings.Port={hostSettings.Port}");
                Line($"ClientSettings.HostSettings.QueryPort={hostSettings.QueryPort}");
            }
            var general = client?.General;
            Line($"ClientSettings.General={(general == null ? "null" : "non-null")}");
            if (general != null)
                Line($"ClientSettings.General.ServerPath={Quote(general.ServerPath)}");
        }
        catch (Exception e)
        {
            Line($"ClientSettings dump threw: {Brief(e)}");
        }
    }

    private static void DumpSceneObjects(bool includeBootstrap)
    {
        if (includeBootstrap)
        {
            try
            {
                var bootstrap = UnityObject.FindObjectOfType<GameBootstrap>();
                if (bootstrap == null)
                    Line("FindObjectOfType<GameBootstrap>=null");
                else
                    DumpMembers(bootstrap, "GameBootstrap(FindObjectOfType)");
            }
            catch (Exception e)
            {
                Line($"FindObjectOfType<GameBootstrap> threw: {Brief(e)}");
            }
        }

        try
        {
            var view = UnityObject.FindObjectOfType<ConnectingView>();
            if (view == null)
            {
                Line("ConnectingView=null (not on this scene yet)");
            }
            else
            {
                Line("ConnectingView=non-null");
                DumpMembers(view, "ConnectingView");
            }
        }
        catch (Exception e)
        {
            Line($"FindObjectOfType<ConnectingView> threw: {Brief(e)}");
        }
    }

    private static void DumpMembers(object target, string prefix)
    {
        var type = target.GetType();
        foreach (var prop in type.GetProperties(DeclaredInstance))
        {
            if (!CanDumpProperty(prop)) continue;
            DumpOne($"{prefix}.{prop.Name}", () => prop.GetValue(target), nestedStructDepth: 1);
        }

        foreach (var field in type.GetFields(DeclaredInstance))
        {
            if (!CanDumpField(field)) continue;
            DumpOne($"{prefix}.{field.Name}", () => field.GetValue(target), nestedStructDepth: 1);
        }
    }

    private static void DumpStaticMembers(Type type, string prefix)
    {
        foreach (var prop in type.GetProperties(DeclaredStatic))
        {
            if (!CanDumpProperty(prop)) continue;
            DumpOne($"{prefix}.{prop.Name}", () => prop.GetValue(null), nestedStructDepth: 0);
        }

        foreach (var field in type.GetFields(DeclaredStatic))
        {
            if (!CanDumpField(field)) continue;
            DumpOne($"{prefix}.{field.Name}", () => field.GetValue(null), nestedStructDepth: 0);
        }
    }

    private static void DumpStructFields(object boxed, string prefix, int nestedStructDepth)
    {
        var type = boxed.GetType();
        foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!CanDumpField(field)) continue;
            DumpOne($"{prefix}.{field.Name}", () => field.GetValue(boxed), nestedStructDepth);
        }
    }

    private static void DumpOne(string name, Func<object?> read, int nestedStructDepth)
    {
        object? value;
        try { value = read(); }
        catch (Exception e)
        {
            Line($"{name} threw: {Brief(e)}");
            return;
        }

        Line($"{name}={FormatValue(value, nestedStructDepth)}");
        if (value == null) return;
        if (IsNullableUnboxed(value.GetType()))
        {
            var inner = TryGetNullableValue(value);
            if (inner != null && IsNestedStruct(inner.GetType()))
                DumpStructFields(inner, name + ".Value", Math.Max(nestedStructDepth, 1));
            return;
        }
        if (nestedStructDepth > 0 && IsNestedStruct(value.GetType()))
            DumpStructFields(value, name, nestedStructDepth - 1);
    }

    private static object? TryGetNullableValue(object boxed)
    {
        try
        {
            var type = boxed.GetType();
            var hasField = type.GetField("has_value") ?? type.GetField("HasValue");
            var hasProp = type.GetProperty("has_value") ?? type.GetProperty("HasValue");
            bool? present = hasField != null ? hasField.GetValue(boxed) as bool?
                : hasProp != null ? hasProp.GetValue(boxed) as bool?
                : null;
            if (present == false) return null;
            var inner = type.GetField("value") ?? type.GetField("Value");
            var innerProp = type.GetProperty("value") ?? type.GetProperty("Value");
            return inner != null ? inner.GetValue(boxed)
                : innerProp != null ? innerProp.GetValue(boxed)
                : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool CanDumpProperty(PropertyInfo prop)
    {
        if (prop.GetIndexParameters().Length != 0) return false;
        if (!prop.CanRead) return false;
        if (SkipName(prop.Name)) return false;
        return true;
    }

    private static bool CanDumpField(FieldInfo field)
    {
        if (field.IsLiteral) return false;
        if (SkipName(field.Name)) return false;
        return true;
    }

    private static bool SkipName(string name)
    {
        if (string.IsNullOrEmpty(name)) return true;
        if (name.StartsWith("NativeFieldInfoPtr", StringComparison.Ordinal)) return true;
        if (name.StartsWith("NativeMethodInfoPtr", StringComparison.Ordinal)) return true;
        if (name is "Pointer" or "WasCollected" or "ObjectClass" or "m_CachedPtr") return true;
        return false;
    }

    private static bool IsNestedStruct(Type type)
    {
        if (!type.IsValueType || type.IsPrimitive || type.IsEnum) return false;
        if (type == typeof(decimal)) return false;
        if (IsFixedString(type)) return false;
        return true;
    }

    private static bool IsFixedString(Type type)
    {
        var name = type.Name;
        return name.StartsWith("FixedString", StringComparison.Ordinal);
    }

    private static string FormatValue(object? value, int nestedStructDepth)
    {
        if (value is null) return "null";

        try
        {
            if (value is UnityObject uo)
                return uo ? $"non-null({uo.GetType().Name} name={Quote(uo.name)})" : "null";
        }
        catch { /* not a UnityEngine.Object, or name threw */ }

        var type = value.GetType();
        if (type.IsEnum)
            return value.ToString() ?? type.Name;

        switch (value)
        {
            case bool b: return b ? "True" : "False";
            case string s: return Quote(s);
            case char c: return Quote(c.ToString());
            case IFormattable formattable when type.IsPrimitive || value is decimal:
                return formattable.ToString(null, CultureInfo.InvariantCulture) ?? "null";
        }

        if (IsFixedString(type) || type == typeof(ConnectAddress))
        {
            try
            {
                var text = value.ToString();
                return text == null ? "null" : Quote(text);
            }
            catch (Exception e)
            {
                return $"ToString threw: {Brief(e)}";
            }
        }

        if (IsNullableUnboxed(type))
            return FormatNullableUnboxed(value);

        if (TryFormatList(value, type, out var listText))
            return listText;

        if (!type.IsValueType)
        {
            try
            {
                // Il2Cpp objects use a custom null via Pointer == 0; ToString is unsafe here.
                return "non-null(" + type.Name + ")";
            }
            catch
            {
                return "non-null";
            }
        }

        if (nestedStructDepth > 0)
            return "struct(" + type.Name + ")";

        try
        {
            var text = value.ToString();
            return text == null ? type.Name : Quote(text);
        }
        catch (Exception e)
        {
            return type.Name + " ToString threw: " + Brief(e);
        }
    }

    private static bool IsNullableUnboxed(Type type)
    {
        var name = type.Name;
        return name.StartsWith("Nullable_Unboxed", StringComparison.Ordinal)
               || (type.IsGenericType && type.Name.StartsWith("Nullable_Unboxed", StringComparison.Ordinal));
    }

    private static string FormatNullableUnboxed(object value)
    {
        try
        {
            var type = value.GetType();
            var has = type.GetField("has_value") ?? type.GetField("HasValue")
                      ?? (FieldInfo?)null;
            var hasProp = type.GetProperty("has_value") ?? type.GetProperty("HasValue");
            bool? present = null;
            if (has != null) present = has.GetValue(value) as bool?;
            else if (hasProp != null) present = hasProp.GetValue(value) as bool?;

            if (present == false) return "null (HasValue=false)";

            var inner = type.GetField("value") ?? type.GetField("Value");
            var innerProp = type.GetProperty("value") ?? type.GetProperty("Value");
            object? boxed = inner != null ? inner.GetValue(value)
                : innerProp != null ? innerProp.GetValue(value)
                : null;
            if (boxed == null) return present == true ? "HasValue=true value=null" : "nullable (unread)";
            return "HasValue=true value=" + FormatValue(boxed, nestedStructDepth: 0);
        }
        catch (Exception e)
        {
            return "nullable threw: " + Brief(e);
        }
    }

    private static bool TryFormatList(object value, Type type, out string text)
    {
        text = "";
        if (!type.Name.StartsWith("List", StringComparison.Ordinal)
            && type.GetInterface("IList") == null
            && !typeof(System.Collections.IEnumerable).IsAssignableFrom(type))
            return false;
        if (type == typeof(string)) return false;

        try
        {
            var countProp = type.GetProperty("Count") ?? type.GetProperty("Length");
            if (countProp == null) return false;
            var countObj = countProp.GetValue(value);
            if (countObj is not int count)
                return false;

            var names = new List<string>(Math.Min(count, 32));
            var item = type.GetProperty("Item");
            for (int i = 0; i < count && i < 32; i++)
            {
                object? entry = null;
                try
                {
                    entry = item != null ? item.GetValue(value, new object[] { i }) : null;
                }
                catch (Exception e)
                {
                    names.Add($"[{i}] threw:{Brief(e)}");
                    continue;
                }

                names.Add(FormatListItem(entry));
            }

            var extra = count > 32 ? $", … +{count - 32}" : "";
            text = $"non-null(count={count} [{string.Join(", ", names)}{extra}])";
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string FormatListItem(object? entry)
    {
        if (entry is null) return "null";
        try
        {
            if (entry is UnityObject uo)
                return uo ? Quote(uo.name) : "null";
        }
        catch { }
        try
        {
            var nameProp = entry.GetType().GetProperty("name") ?? entry.GetType().GetProperty("Name");
            if (nameProp != null)
            {
                var n = nameProp.GetValue(entry) as string;
                if (!string.IsNullOrEmpty(n)) return Quote(n);
            }
        }
        catch { }
        return FormatValue(entry, nestedStructDepth: 0);
    }

    // ---- log / file ---------------------------------------------------------------------------

    private static void Header(string reason)
    {
        Line("========== " + reason + " ==========");
    }

    private static void TryLine(string name, Func<string> read)
    {
        try { Line($"{name}={read()}"); }
        catch (Exception e) { Line($"{name} threw: {Brief(e)}"); }
    }

    private static void Line(string text)
    {
        var seq = System.Threading.Interlocked.Increment(ref _dumpSeq);
        var stamp = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture) + "Z";
        var body = text ?? "";
        // File only (docs/bridge.md, "Logging policy"): a dump is dozens of lines on every connect.
        try { Plugin.Logger?.LogDiag($"{LogPrefix} {body}"); }
        catch { /* logger may be gone during Unload */ }

        try
        {
            var path = CapturePath;
            var line = $"{stamp} #{seq} {body}{Environment.NewLine}";
            lock (Gate)
            {
                File.AppendAllText(path, line);
            }
        }
        catch (Exception e)
        {
            try { Plugin.Logger?.LogWarning($"{LogPrefix} file append failed: {e.Message}"); }
            catch { /* ignore */ }
        }
    }

    private static object Status(bool dumped, int dumpLines, bool? enable)
    {
        bool installed;
        lock (Gate) { installed = _bound; }
        return new
        {
            ok = true,
            installed,
            startPatchable = _startPatchable,
            startPatched = _startPatched,
            awakePatched = _awakePatched,
            launchGamePatched = _launchGamePatched,
            startPatchError = _startPatchError,
            awakePatchError = _awakePatchError,
            launchGamePatchError = _launchGamePatchError,
            capturePath = CapturePath,
            dumped,
            dumpLines,
            enable,
            note = "Prefixes log to BepInEx (prefix host_capture:) and append %TEMP%\\progenitor-host-capture.txt. "
                   + "Click Private Game / Host by hand, read the capture, then run the host op and diff.",
        };
    }

    private static string Quote(string? s)
    {
        if (s == null) return "null";
        if (s.Length > StringCap)
            s = s.Substring(0, StringCap) + "…";
        var sb = new StringBuilder(s.Length + 2);
        sb.Append('"');
        foreach (var ch in s)
        {
            if (ch is '"' or '\\') sb.Append('\\');
            if (ch is '\r' or '\n' or '\t')
            {
                sb.Append(ch == '\n' ? "\\n" : ch == '\r' ? "\\r" : "\\t");
                continue;
            }
            sb.Append(ch);
        }
        sb.Append('"');
        return sb.ToString();
    }

    private static string Brief(Exception e)
    {
        var inner = e is TargetInvocationException { InnerException: { } ie } ? ie : e;
        return inner.GetType().Name + ": " + (inner.Message ?? "");
    }

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            return null;
        try { return el.GetString(); }
        catch { return null; }
    }

    private static bool? GetOptionalBool(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        try
        {
            if (el.ValueKind == JsonValueKind.True) return true;
            if (el.ValueKind == JsonValueKind.False) return false;
            if (el.ValueKind == JsonValueKind.String && bool.TryParse(el.GetString(), out var s))
                return s;
        }
        catch { }
        return null;
    }
}
