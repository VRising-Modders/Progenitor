using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using HarmonyLib;
using ProjectM;
using Progenitor.Reload;
using Stunlock.Core;
using Stunlock.Localization;
using GameLocalization = Stunlock.Localization.Localization;

namespace Progenitor.Localization;

/// <summary>
/// Lets a mod own strings the game will look up by <see cref="LocalizationKey"/>, which is how
/// item names, tooltips, and similar UI text are resolved. Vanilla keys are Unity asset GUIDs;
/// a mod string has no asset, so the GUID is derived from the key (MD5 of the UTF-8 bytes packed
/// into <see cref="AssetGuid"/>'s four ints). That value is stable across launches, so a cloned
/// <see cref="ManagedItemData"/> can keep the same <see cref="ManagedItemData.Name"/> forever.
///
/// The game table (<see cref="GameLocalization.LocalizedStrings"/>) is
/// <c>Dictionary&lt;AssetGuid, string&gt;</c> for the current language only. <see cref="AssetGuid"/>
/// is four ints with no reference fields, so it stays a blittable struct in the interop and the
/// indexer is safe to call; the PrefabLookupKey trap does not apply. Inserts are client-only:
/// the server has no string table, so <see cref="Register"/> just records. Language changes wipe
/// that table, which is why a Harmony postfix on <see cref="GameLocalization.SetLanguage"/>
/// writes the matching (or English-fallback) text back in. The postfix is installed on first
/// client <see cref="Register"/> so this module does not have to be wired from Plugin.Load.
///
/// Every registration is <see cref="IDisposable"/> so a reloadable plugin can drop its strings
/// in Unload; <see cref="RemoveAll"/> does the same by owner. File drops go through
/// <see cref="LoadDirectory"/> / <see cref="LoadForPlugin"/> so a translator can edit JSON next
/// to the DLL without a rebuild; those calls still end in <see cref="Register"/> so dispose and
/// owner cleanup stay on one path.
/// </summary>
public static class Localization
{
    private static readonly object Gate = new();
    private static readonly List<Entry> Entries = new();
    private static bool _hooked;
    private static int _hookFailureLogged;

    /// <summary>
    /// Called from Core Install so SkipCoreModules can log a skip. The SetLanguage postfix
    /// is still lazy on first client <see cref="Register"/>; skip is honored there too.
    /// </summary>
    internal static void Install()
    {
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.LocalizationModule))
            Plugin.Logger?.LogInfo(HarmonySlotHandoff.SkipModuleLog(HarmonySlotHandoff.LocalizationModule));
    }

    /// <summary>
    /// What this module registered that can outlive a collectible Core generation
    /// (SetLanguage Harmony slot / fallback, live string table).
    /// </summary>
    internal static string DescribePins()
    {
        var holds = new List<string>();
        try
        {
            lock (Gate)
            {
                if (_hooked) holds.Add("SetLanguageHook");
                if (Entries.Count > 0) holds.Add("entries=" + Entries.Count);
            }
        }
        catch (Exception e)
        {
            holds.Add("threw:" + e.Message);
        }
        return HarmonySlotHandoff.FormatModulePins(HarmonySlotHandoff.LocalizationModule, holds);
    }

    /// <summary>
    /// Record <paramref name="text"/> under <paramref name="key"/> for <paramref name="language"/>.
    /// On the client, if that language is the one currently loaded, the string is also written
    /// into the game's table so <c>Localization.Get</c> can see it. Dispose to remove this
    /// variant; a later registration of the same key and language takes precedence until then.
    /// <paramref name="owner"/> is optional and lets <see cref="RemoveAll"/> drop every string
    /// a plugin added in one call.
    /// </summary>
    public static IDisposable Register(string key, string text, string language = "English", object? owner = null)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        if (text == null) throw new ArgumentNullException(nameof(text));
        language = LocalizationParser.CanonicalLanguage(language);

        var guid = GuidFor(key);
        var handle = new Registration();
        var entry = new Entry
        {
            Key = key,
            Language = language,
            Text = text,
            Guid = guid,
            Owner = owner,
            Handle = handle,
        };
        handle.Bind(entry);

        // Job FLATCH1 (R44 finding 6). This used to `return NoOpHandle.Instance` from inside the
        // loop, skipping EnsureHooked and PushKeyToGame. That reads our own Entries list as
        // evidence about the GAME's LocalizedStrings — two different lifetimes. The game table is
        // rebuilt by GameLocalization.SetLanguage (and does not exist at all before the game's
        // own Localization initializes), so "we already have this row" says nothing about whether
        // the string is in the table. A retry is named in the comment below as an expected caller
        // and a retry is exactly what a mod does after its strings vanish. Both calls now happen
        // on every path; the duplicate check only decides whether a ROW is added.
        bool duplicate = false;
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                var existing = Entries[i];
                if (existing.Key != key || !LanguagesEqual(existing.Language, language))
                    continue;
                // Same owner + same text (definition Register then apply, or a retry)
                // must not stack a second row: Dispose of one would leave the other,
                // and soft unapply must not drop a key the plugin still owns.
                if (existing.Text == text && owner != null && ReferenceEquals(existing.Owner, owner))
                {
                    duplicate = true;
                    break;
                }
                // Same text twice (a plugin's en.json plus a Named() on the same id) is not a conflict.
                if (existing.Text == text) break;
                Plugin.Logger?.LogWarning(
                    $"Localization: key '{key}' language '{language}' was already registered; the new text takes precedence until it is disposed.");
                break;
            }
            if (!duplicate) Entries.Add(entry);
        }

        EnsureHooked();
        PushKeyToGame(key);
        return duplicate ? NoOpHandle.Instance : handle;
    }

    /// <summary>
    /// Drop every registration whose owner is <paramref name="owner"/>. Returns how many were
    /// removed so a plugin can log that Unload actually released them.
    /// </summary>
    public static int RemoveAll(object owner)
    {
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        List<Registration> handles;
        lock (Gate)
        {
            handles = new List<Registration>();
            for (int i = 0; i < Entries.Count; i++)
            {
                if (ReferenceEquals(Entries[i].Owner, owner))
                    handles.Add(Entries[i].Handle);
            }
        }

        for (int i = 0; i < handles.Count; i++)
            handles[i].Dispose();
        return handles.Count;
    }

    /// <summary>
    /// Read every <c>*.json</c> in <paramref name="dir"/> and <see cref="Register"/> each
    /// key-to-string entry. The file stem is the language (<c>en.json</c>, <c>de.json</c>);
    /// <see cref="LocalizationParser.CanonicalLanguage"/> maps ISO codes onto the Steam names
    /// the game uses so an English client actually sees <c>en</c> strings. One disposable
    /// reverses the whole directory so a reloadable plugin does not have to track each key.
    /// Missing directories are a no-op; malformed files are logged and skipped.
    /// </summary>
    public static IDisposable LoadDirectory(string dir, object owner)
    {
        if (string.IsNullOrEmpty(dir)) throw new ArgumentException("Directory is required.", nameof(dir));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        var handles = new List<IDisposable>();
        try
        {
            if (!Directory.Exists(dir))
                return new CompositeHandle(handles);

            string[] files;
            try
            {
                files = Directory.GetFiles(dir, "*.json");
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"Localization: could not list '{dir}': {e.Message}");
                return new CompositeHandle(handles);
            }

            Array.Sort(files, StringComparer.OrdinalIgnoreCase);
            for (int i = 0; i < files.Length; i++)
            {
                try
                {
                    LoadFile(files[i], owner, handles);
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning($"Localization: skipped '{files[i]}': {e.Message}");
                }
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization.LoadDirectory({dir}) failed: {e}");
        }

        return new CompositeHandle(handles);
    }

    /// <summary>
    /// Load JSON translations next to <paramref name="asm"/>. Reloadable plugins are loaded from
    /// a byte stream, so <see cref="Assembly.Location"/> is empty; the directory then comes from
    /// <see cref="PluginReloader.TryGetSourcePath"/>. Probes
    /// <c>&lt;dir&gt;/&lt;AssemblyName&gt;/localization</c> then <c>&lt;dir&gt;/localization</c>
    /// and loads the first that exists. Logs the chosen folder at info; logs nothing when neither
    /// exists so a plugin without translations is not a warning. Owner is the plugin instance so
    /// <see cref="RemoveAll"/> and the returned disposable both undo the load.
    /// </summary>
    public static IDisposable LoadForPlugin(Assembly asm, object owner)
    {
        if (asm == null) throw new ArgumentNullException(nameof(asm));
        if (owner == null) throw new ArgumentNullException(nameof(owner));

        try
        {
            var location = asm.Location;
            if (string.IsNullOrEmpty(location)
                && (!PluginReloader.TryGetSourcePath(asm, out location) || string.IsNullOrEmpty(location)))
            {
                Plugin.Logger?.LogWarning(
                    $"Localization: assembly '{asm.GetName().Name}' has no Location and no reload source path; cannot load files.");
                return new CompositeHandle(new List<IDisposable>());
            }

            var dllDir = Path.GetDirectoryName(location) ?? "";
            var name = asm.GetName().Name ?? "";
            var dir = LocalizationParser.ResolvePluginFolder(dllDir, name, Directory.Exists);
            if (dir == null)
                return new CompositeHandle(new List<IDisposable>());

            Plugin.Logger?.LogInfo($"Localization: loading '{name}' from '{dir}'.");
            return LoadDirectory(dir, owner);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization.LoadForPlugin failed: {e}");
            return new CompositeHandle(new List<IDisposable>());
        }
    }

    /// <summary>
    /// The <see cref="LocalizationKey"/> for <paramref name="key"/>, derived the same way
    /// <see cref="Register"/> stores it. Safe to call before registering; the game will only
    /// resolve a name once the string is in its table.
    /// </summary>
    public static LocalizationKey KeyFor(string key)
    {
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        return new LocalizationKey(GuidFor(key));
    }

    /// <summary>
    /// Reads a string the game already knows, including ones this module inserted. Does not
    /// consult the Progenitor-side dictionary, so it is a true view of what UI code will see.
    /// Always false on the server (no string table) and when the key is missing.
    /// </summary>
    public static bool TryGetGameString(LocalizationKey key, out string text)
    {
        text = "";
        if (Plugin.IsServerProcess) return false;
        try
        {
            if (!GameLocalization.HasKey(key.Key)) return false;
            var value = GameLocalization.Get(key, logIfNotExists: false);
            if (string.IsNullOrEmpty(value)) return false;
            text = value;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization.TryGetGameString failed: {e}");
            return false;
        }
    }

    /// <summary>
    /// Point a cloned <see cref="ManagedItemData"/> at a registered key so inventory UI shows
    /// the mod's name instead of the source prefab's. Only writes <see cref="ManagedItemData.Name"/>;
    /// the string itself must already have been <see cref="Register"/>ed on the client.
    /// </summary>
    public static void ApplyName(ManagedItemData data, string key)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        try
        {
            data.Name = KeyFor(key);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization.ApplyName({key}) failed: {e}");
        }
    }

    /// <summary>
    /// Point a cloned <see cref="ManagedItemData"/> at a registered key so inventory UI shows
    /// the mod's description instead of the source prefab's. Writes the
    /// <c>LocalizationKey</c> on <see cref="ManagedItemData.Description"/> (the sibling of
    /// <see cref="ManagedItemData.Name"/>; the field type is
    /// <c>ProjectM.UI.LocalizedStringBuilderBase</c>). The string itself must already have been
    /// <see cref="Register"/>ed, typically under <c>id.desc</c>. Format parameters inherited
    /// from the source clone are cleared: a mod description is a plain string, not a templated
    /// vanilla tooltip.
    /// </summary>
    public static void ApplyDescription(ManagedItemData data, string key)
    {
        if (data == null) throw new ArgumentNullException(nameof(data));
        if (string.IsNullOrEmpty(key)) throw new ArgumentException("Key is required.", nameof(key));
        try
        {
            var description = data.Description;
            description.Key = KeyFor(key);
            description.ParameterIndex = 0;
            description.ParameterCount = 0;
            data.Description = description;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization.ApplyDescription({key}) failed: {e}");
        }
    }

    /// <summary>
    /// True when <paramref name="key"/> has at least one language recorded in this module,
    /// including strings loaded from a plugin's JSON. Does not consult the game table, so it
    /// is true on the server (where there is no string table) as long as <see cref="Register"/>
    /// ran. Used by the prefab applicator to honor a file-based <c>id.desc</c> without a
    /// matching <c>Described()</c>.
    /// </summary>
    public static bool IsRegistered(string key)
    {
        if (string.IsNullOrEmpty(key)) return false;
        lock (Gate)
        {
            for (int i = 0; i < Entries.Count; i++)
            {
                if (Entries[i].Key == key) return true;
            }
        }
        return false;
    }

    /// <summary>
    /// MD5 of the UTF-8 key packed into <see cref="AssetGuid"/>'s four-int constructor. Same
    /// key always yields the same GUID, which is the whole point of not calling
    /// <see cref="AssetGuid.NewGuid"/>. Fields are written after the constructor so the layout
    /// is correct even if the native ctor is a no-op through the interop.
    /// </summary>
    internal static AssetGuid GuidFor(string key)
    {
        var hash = MD5.HashData(Encoding.UTF8.GetBytes(key));
        int a = BitConverter.ToInt32(hash, 0);
        int b = BitConverter.ToInt32(hash, 4);
        int c = BitConverter.ToInt32(hash, 8);
        int d = BitConverter.ToInt32(hash, 12);
        return new AssetGuid(a, b, c, d) { _a = a, _b = b, _c = c, _d = d };
    }

    private static void Drop(Entry entry)
    {
        bool removed;
        lock (Gate)
        {
            removed = Entries.Remove(entry);
        }
        if (!removed) return;
        PushKeyToGame(entry.Key);
    }

    private static void PushKeyToGame(string key)
    {
        if (Plugin.IsServerProcess) return;

        // Read CurrentLanguage before taking Gate: a getter that re-entered SetLanguage
        // would otherwise deadlock the postfix.
        string language = CurrentOrDefault();
        AssetGuid guid;
        string? text;
        lock (Gate)
        {
            guid = GuidFor(key);
            text = ResolveText(key, language);
        }

        try
        {
            var table = GameTable();
            if (table == null) return;
            if (text == null) table.Remove(guid);
            else table[guid] = text;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization: failed to update LocalizedStrings for '{key}': {e}");
        }
    }

    private static void PushAllToGame(string? language)
    {
        if (Plugin.IsServerProcess) return;

        string resolved = string.IsNullOrEmpty(language) ? CurrentOrDefault() : language!;
        List<(AssetGuid guid, string? text)> snapshot;
        lock (Gate)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            snapshot = new List<(AssetGuid, string?)>(Entries.Count);
            for (int i = 0; i < Entries.Count; i++)
            {
                var key = Entries[i].Key;
                if (!seen.Add(key)) continue;
                snapshot.Add((Entries[i].Guid, ResolveText(key, resolved)));
            }
        }

        try
        {
            var table = GameTable();
            if (table == null) return;
            for (int i = 0; i < snapshot.Count; i++)
            {
                var (guid, text) = snapshot[i];
                if (text == null) table.Remove(guid);
                else table[guid] = text;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization: failed to re-apply strings after SetLanguage({language}): {e}");
        }
    }

    /// <summary>
    /// Last registration for <paramref name="language"/> wins; if none, last English / <c>en</c>
    /// registration. That fallback keeps a cloned item from showing a missing-key blank when the
    /// player is not on a language the mod shipped.
    /// </summary>
    private static string? ResolveText(string key, string language)
    {
        string? match = null;
        string? english = null;
        for (int i = 0; i < Entries.Count; i++)
        {
            var entry = Entries[i];
            if (entry.Key != key) continue;
            if (LanguagesEqual(entry.Language, language)) match = entry.Text;
            else if (LocalizationParser.IsFallbackLanguage(entry.Language)) english = entry.Text;
        }
        return match ?? english;
    }

    private static Il2CppSystem.Collections.Generic.Dictionary<AssetGuid, string>? GameTable()
    {
        try
        {
            var table = GameLocalization.LocalizedStrings;
            if (table != null) return table;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Localization: LocalizedStrings getter failed ({e.Message}); trying _LocalizedStrings.");
        }

        try
        {
            return GameLocalization._LocalizedStrings;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"Localization: _LocalizedStrings was not readable: {e}");
            return null;
        }
    }

    private static string CurrentOrDefault()
    {
        try
        {
            var current = GameLocalization.CurrentLanguage;
            if (!string.IsNullOrEmpty(current)) return current;
        }
        catch
        {
            // Localization may not be initialized yet; Register still records.
        }

        try
        {
            var fallback = GameLocalization.DefaultLanguage;
            if (!string.IsNullOrEmpty(fallback)) return fallback;
        }
        catch
        {
        }

        return "English";
    }

    private static void LoadFile(string file, object owner, List<IDisposable> handles)
    {
        string json;
        try
        {
            json = File.ReadAllText(file);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"Localization: could not read '{file}': {e.Message}");
            return;
        }

        if (!LocalizationParser.TryParse(json, out var entries, out var error))
        {
            Plugin.Logger?.LogWarning($"Localization: malformed JSON in '{file}': {error}");
            return;
        }

        var stem = LocalizationParser.LanguageFromFileName(file);
        if (string.IsNullOrEmpty(stem))
        {
            Plugin.Logger?.LogWarning($"Localization: skipped '{file}'; file name has no language code.");
            return;
        }

        var language = LocalizationParser.CanonicalLanguage(stem);
        int loaded = 0;
        foreach (var kv in entries)
        {
            try
            {
                handles.Add(Register(kv.Key, kv.Value, language, owner));
                loaded++;
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"Localization: skipped key '{kv.Key}' in '{file}': {e.Message}");
            }
        }

        Plugin.Logger?.LogInfo(
            $"Localization: loaded {loaded} keys for '{language}' from '{Path.GetFileName(file)}'.");
    }

    private static bool LanguagesEqual(string a, string b) =>
        string.Equals(
            LocalizationParser.CanonicalLanguage(a),
            LocalizationParser.CanonicalLanguage(b),
            StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Installed on first client Register. Uses Core's Harmony id so Plugin.UnpatchSelf also
    /// tears this postfix down; the module is not wired from Plugin.cs.
    /// </summary>
    internal delegate void SetLanguage(string language);

    /// <summary>
    /// Job FLATCH1 (R44 finding 6). <c>_hooked</c> means "the SetLanguage postfix is installed
    /// for this Core generation", and it is now set on exactly one line, gated by
    /// <see cref="HarmonySlotHandoff.BindMayLatch"/> plus a confirmation that the patch really
    /// landed. It used to be set on the Inert path (Bootstrap owns the site, the slot write
    /// FAILED), the Vanilla path (nothing is patched at all), the SkipCoreHarmony path and the
    /// default path — four ways to record "hooked" for a seam that was never installed. Losing
    /// this seam is not cosmetic: a language change from the settings UI rebuilds
    /// <c>LocalizedStrings</c> and wipes every mod entry, and <see cref="PushAllToGame"/> is
    /// reachable only from the postfix that was never installed.
    ///
    /// <para><b>Re-arm boundary.</b> Every <see cref="Register"/> retries until an install
    /// lands — this module is lazy on first Register by design, so Register is its only clock —
    /// and the flag itself dies with the Core generation whose patch it describes. Nothing here
    /// latches on a condition that can go away on its own: <c>SkipCoreHarmony</c> flips back off
    /// when a live world refuses the skip, and a refused slot write can be accepted later. The
    /// error is logged once per generation so the retry does not flood the log.</para>
    /// </summary>
    private static void EnsureHooked()
    {
        if (_hooked || Plugin.IsServerProcess) return;
        if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.LocalizationModule))
            return;
        lock (Gate)
        {
            if (_hooked || Plugin.IsServerProcess) return;
            if (HarmonySlotHandoff.ShouldSkipModule(HarmonySlotHandoff.LocalizationModule))
                return;
            // The declared diagnostics opt-out. Not a latch: SkipRefusedLiveWorld turns
            // SkipCoreHarmony back off when a live world refuses it, and the next Register
            // must install the postfix then.
            if (HarmonySlotHandoff.SkipCoreHarmony) return;

            HarmonySlotHandoff.SiteBind outcome;
            try
            {
                outcome = HarmonySlotHandoff.Bind(
                    HarmonySlotHandoff.SetLanguage, new SetLanguage(SetLanguageHook.Postfix));
            }
            catch (Exception e)
            {
                LogHookFailureOnce($"Localization: could not patch SetLanguage: {e}");
                return;
            }

            bool installed = false;
            switch (outcome)
            {
                case HarmonySlotHandoff.SiteBind.Filled:
                    Plugin.Logger?.LogInfo("Localization: postfix installed on SetLanguage.");
                    installed = true;
                    break;
                case HarmonySlotHandoff.SiteBind.Fallback:
                    Plugin.Logger?.LogInfo(HarmonySlotHandoff.FallbackSiteLog(HarmonySlotHandoff.SetLanguage));
                    // Bind only clears Core to patch; FallbackSetLanguage says whether it did.
                    //
                    // Job FLATCH2 (R45 finding 7). Its own guard, because FLATCH1 narrowed the
                    // catch above to Bind alone and hoisted this call out of the region that used
                    // to cover it. What is inside here is HarmonyX PatchAll, AccessTools.Method
                    // over an IL2CPP interop type and Patches.Install — exactly the calls the
                    // original catch existed for. An escape does not stop at a missing log line:
                    // the only caller is Register, which has already added the row under Gate, so
                    // the throw skips PushKeyToGame, never returns the handle, and leaves a
                    // Core-held row whose Owner is the calling plugin — a pin across an ALC
                    // unload, from a Core registration call, inside that plugin's Load. The latch
                    // semantics are unchanged: installed stays false, so _hooked is not set and
                    // the next Register retries.
                    try { installed = FallbackSetLanguage(); }
                    catch (Exception e)
                    {
                        installed = false;
                        LogHookFailureOnce($"Localization: could not patch SetLanguage: {e}");
                        break;
                    }
                    if (!installed)
                        LogHookFailureOnce(null);
                    break;
                case HarmonySlotHandoff.SiteBind.Inert:
                    LogHookFailureOnce(HarmonySlotHandoff.InertLog("Localization", HarmonySlotHandoff.SetLanguage));
                    break;
                case HarmonySlotHandoff.SiteBind.Vanilla:
                    LogHookFailureOnce(HarmonySlotHandoff.VanillaLog("Localization", HarmonySlotHandoff.SetLanguage));
                    break;
                default:
                    LogHookFailureOnce("Localization: SetLanguage bind returned " + outcome + ".");
                    break;
            }

            // The only place _hooked is set.
            if (installed && HarmonySlotHandoff.BindMayLatch(outcome)) _hooked = true;
        }
    }

    /// <summary>
    /// One line per Core generation for a SetLanguage seam that did not install, plus what the
    /// player will see. <see cref="EnsureHooked"/> keeps retrying on every <see cref="Register"/>;
    /// the log does not repeat. <paramref name="detail"/> may be null when the failing call has
    /// already logged its own reason.
    /// </summary>
    private static void LogHookFailureOnce(string? detail)
    {
        if (Interlocked.Exchange(ref _hookFailureLogged, 1) != 0) return;
        if (!string.IsNullOrEmpty(detail)) Plugin.Logger?.LogError(detail);
        Plugin.Logger?.LogError(
            "Localization: the SetLanguage postfix is NOT installed for this Core generation; a "
            + "language change from the settings UI will wipe mod strings until they are "
            + "re-registered. Every Register retries the install; this line is logged once.");
    }

    /// <summary>Core's registry owner id; every Core slot registration shares it (job FHARM8).</summary>
    internal const string OwnerId = MyPluginInfo.PLUGIN_GUID + Progenitor.Patches.PatchSitePolicy.CoreOwnerSuffix;

    /// <summary>
    /// Job FHARM8. The <c>SetLanguage</c> fallback, taken whenever Bootstrap does not own that
    /// site. Before FHARM8 it was an unconditional <c>PatchAll(typeof(SetLanguageHook))</c>,
    /// which handed HarmonyX a <c>Progenitor.Core</c> method and pinned this Core generation for
    /// the life of the process even after a clean unpatch (<c>harmony-patches.md</c> section 8).
    ///
    /// <para><b>The shape fits.</b> The body is a postfix that reads one by-value argument and
    /// nothing else: no <c>__result</c>, no <c>__state</c>. The one thing the slot changes is
    /// that <c>language</c> has to be found in <c>__args</c> by the original's own parameter name
    /// rather than bound by Harmony — <see cref="Progenitor.Patches.PatchRegistry.ArgIndex"/>, so
    /// a game update that inserts a parameter moves the index with it and one that renames it
    /// leaves the seam unpatched with a line naming it, never a guessed position.</para>
    ///
    /// <para>Losing this seam means a language change from the settings UI rebuilds
    /// <c>LocalizedStrings</c> from the game's file and wipes mod entries until the next
    /// <c>PushAllToGame</c>, so the refusal says so rather than logging "installed".</para>
    /// </summary>
    /// <returns>
    /// True only when the postfix is actually on the method. Job FLATCH1: the caller latches
    /// <c>_hooked</c> on this answer, so every failure path below must return false rather than
    /// leaving the caller to assume the Fallback route succeeded.
    /// </returns>
    private static bool FallbackSetLanguage()
    {
        var route = Progenitor.Patches.CorePatchFallbackPolicy.Decide(
            slotShapeFits: true,
            bootstrapPresent: Progenitor.Patches.PatchRegistry.SlotsAvailable);

        if (route == Progenitor.Patches.CoreFallbackRoute.Refused)
        {
            // Unreachable while slotShapeFits is true; handled so a future body that needs
            // __result cannot fall through to a silent permanent pin.
            Plugin.Logger?.LogError(Progenitor.Patches.CorePatchFallbackPolicy.FormatRefused(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleLocalization,
                HarmonySlotHandoff.SetLanguage));
            return false;
        }

        if (route == Progenitor.Patches.CoreFallbackRoute.Direct)
        {
            Plugin.Logger?.LogWarning(Progenitor.Patches.CorePatchFallbackPolicy.FormatDirect(
                Progenitor.Patches.CorePatchFallbackPolicy.ModuleLocalization,
                HarmonySlotHandoff.SetLanguage,
                Progenitor.WorldState.WorldEpoch.CoreGenerationOrZero()));
            HarmonyFallback.Ensure().PatchAll(typeof(SetLanguageHook));
            Plugin.Logger?.LogInfo("Localization: postfix installed on SetLanguage.");
            return true;
        }

        var original = AccessTools.Method(
            typeof(GameLocalization), nameof(GameLocalization.SetLanguage));
        if (original == null)
        {
            Plugin.Logger?.LogError(
                "Localization: GameLocalization.SetLanguage was not found on the interop type; a "
                + "language change from the settings UI will wipe mod strings until the next push.");
            return false;
        }

        int index = Progenitor.Patches.PatchRegistry.ArgIndex(original, "language");
        if (index < 0)
        {
            Plugin.Logger?.LogError(Progenitor.Patches.SlotArgs.FormatMissingParameter(
                "GameLocalization.SetLanguage", "language",
                "the SetLanguage seam is left unpatched"));
            return false;
        }

        Progenitor.Patches.Patches.Install();
        if (Progenitor.Patches.PatchRegistry.PostfixSlot(
                owner: null, ownerId: OwnerId, target: original,
                postfix: (_, args) =>
                {
                    string? language = args != null && index < args.Length ? args[index] as string : null;
                    SetLanguageHook.Postfix(language!);
                }) == null)
        {
            Plugin.Logger?.LogError(
                "Localization: the slot-routed fallback for " + HarmonySlotHandoff.SetLanguage
                + " was refused (see the patches: line above); a language change from the settings "
                + "UI will wipe mod strings for this Core generation.");
            return false;
        }

        Plugin.Logger?.LogInfo("Localization: postfix installed on SetLanguage.");
        return true;
    }

    /// <summary>
    /// SetLanguage is private in the game and public on the interop wrapper; Harmony-IL2CPP
    /// detours the native method, so a language change from the settings UI still reaches us.
    /// The original rebuilds LocalizedStrings from the language file, wiping mod entries.
    /// </summary>
    private static class SetLanguageHook
    {
        [HarmonyPatch(typeof(GameLocalization), nameof(GameLocalization.SetLanguage))]
        [HarmonyPostfix]
        internal static void Postfix(string language)
        {
            try
            {
                PushAllToGame(language);
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogError($"Localization SetLanguage postfix: {e}");
            }

            // ctx.Castles.BuildingDisplayName: the kept names were in the old language.
            try { Progenitor.Castles.BuildingDisplayNames.Clear(); }
            catch { /* the next world change clears them too */ }
        }
    }

    private sealed class Entry
    {
        public string Key = "";
        public string Language = "English";
        public string Text = "";
        public AssetGuid Guid;
        public object? Owner;
        public Registration Handle = null!;
    }

    /// <summary>
    /// Returned when <see cref="Register"/> is asked for a key+language+text this owner
    /// already holds. Dispose is a no-op so a definition's apply-time call cannot undo
    /// the registration-time row.
    /// </summary>
    private sealed class NoOpHandle : IDisposable
    {
        public static readonly NoOpHandle Instance = new();
        public void Dispose() { }
    }

    private sealed class Registration : IDisposable
    {
        private Entry? _entry;
        private int _disposed;

        public void Bind(Entry entry) => _entry = entry;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            var entry = _entry;
            _entry = null;
            if (entry != null) Drop(entry);
        }
    }

    private sealed class CompositeHandle : IDisposable
    {
        private readonly List<IDisposable> _inner;
        private int _disposed;

        public CompositeHandle(List<IDisposable> inner) => _inner = inner;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            for (int i = _inner.Count - 1; i >= 0; i--)
            {
                try { _inner[i].Dispose(); }
                catch (Exception e) { Plugin.Logger?.LogWarning($"Localization: registration dispose threw: {e.Message}"); }
            }
        }
    }
}
