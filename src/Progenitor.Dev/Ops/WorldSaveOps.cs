using System;
using System.IO;
using System.Text;
#if !PROGENITOR_TESTS
using System.Text.Json;
using ProjectM;
using Progenitor.Ecs;
using Progenitor.Persistence;
using Unity.Collections;
using Unity.Entities;
#endif
using Progenitor.Hosted;

namespace Progenitor.Dev.Ops;

/// <summary>
/// Server debug-bridge <c>world_save</c>: request a real world save the same way the game
/// does, then flush per-mod JSON so both land together. The existing <c>save</c> op only
/// calls <c>ModSave.SaveAll</c>; it never creates a <c>.save.gz</c>.
///
/// Default <c>fileName</c> is the world's current save file (what the next boot loads),
/// not a distinct <c>WorldSave.save</c> that boot would ignore (R11 finding 12). The live
/// dedicated root is refused the same way extra-instance auth is.
///
/// Fallback entity create: typed <c>Add</c>/<c>Write&lt;T&gt;</c> of the interop struct
/// (<c>sizeof(T)</c>; RawWriteGuard allows that at TypeSize 0). A 512-byte
/// <c>SaveFileName</c> overlay at TypeSize 0 is never used (R12 finding 10). If the typed
/// write throws, the entity is destroyed and the op returns <c>ok: false</c>.
/// </summary>
public static class WorldSaveOps
{
    /// <summary>
    /// UTF-8 capacity of <c>FixedString128Bytes</c> (length prefix + terminator sit
    /// in the other three bytes). Longer names would truncate inside the interop struct.
    /// </summary>
    public const int MaxFileNameUtf8Bytes = Layout.FixedString128MaxUtf8Bytes;

    /// <summary>
    /// Dump layout: <c>SavePersistentDataEvent.CompressSaveFiles</c> at 0x204. Kept so
    /// tests can name the slot; overlay is not a fallback (R12 finding 10).
    /// </summary>
    public const int CompressSaveFilesOffset = Layout.SavePersistentDataCompressSaveFilesOffset;

    /// <summary>Dump layout: <c>SaveFileName</c> starts at offset 4.</summary>
    public const int SaveFileNameOffset = Layout.SavePersistentDataSaveFileNameOffset;

    /// <summary><c>FixedString512Bytes</c> payload the overlay must not memcpy at TypeSize 0.</summary>
    public const int SaveFileNameBytes = Layout.FixedString512PayloadBytes;

    /// <summary>
    /// Leaf name ending in <c>.save</c> for the <c>TriggerSave</c> argument. Strips a
    /// directory, strips a trailing <c>.gz</c> (the game concatenates it when compressing),
    /// and appends <c>.save</c> when missing. Empty uses <paramref name="currentWorldSaveName"/>
    /// (the file the next boot loads). Empty with no current name is refused — a made-up
    /// <c>WorldSave.save</c> is not what boot reloads.
    /// </summary>
    public static string NormalizeSaveFileName(string? requested, string? currentWorldSaveName = null)
    {
        var name = (requested ?? "").Trim();
        if (name.Length == 0)
        {
            name = (currentWorldSaveName ?? "").Trim();
            if (name.Length == 0)
            {
                throw new ArgumentException(
                    "args.fileName is empty and the current world save name is unknown; " +
                    "pass fileName or wait until ServerRuntimeSettings.SaveToLoad is readable");
            }
        }

        name = name.Replace('/', Path.DirectorySeparatorChar)
            .Replace('\\', Path.DirectorySeparatorChar);
        name = Path.GetFileName(name);
        if (string.IsNullOrEmpty(name) || name == "." || name == "..")
            throw new ArgumentException("args.fileName must be a file name, not a path");

        var invalid = Path.GetInvalidFileNameChars();
        if (name.IndexOfAny(invalid) >= 0)
            throw new ArgumentException("args.fileName contains invalid filename characters");

        if (name.EndsWith(".save.gz", StringComparison.OrdinalIgnoreCase))
            name = name.Substring(0, name.Length - 3);
        if (!name.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
            name += ".save";

        int utf8 = Encoding.UTF8.GetByteCount(name);
        if (utf8 > MaxFileNameUtf8Bytes)
        {
            throw new ArgumentException(
                $"args.fileName is {utf8} UTF-8 bytes; FixedString128Bytes holds {MaxFileNameUtf8Bytes}.");
        }

        return name;
    }

    /// <summary>
    /// On-disk name after TriggerSave's optional <c>.gz</c> concatenate. The event's
    /// <c>SaveFileName</c> stores this form, not the pre-compress argument.
    /// </summary>
    public static string DiskFileName(string saveNameEndingInDotSave, bool compress)
    {
        if (string.IsNullOrEmpty(saveNameEndingInDotSave))
            return saveNameEndingInDotSave;
        if (compress && !saveNameEndingInDotSave.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            return saveNameEndingInDotSave + ".gz";
        return saveNameEndingInDotSave;
    }

    /// <summary>
    /// Maps the bridge string to a <c>SaveReason</c> name. Empty is ManualSave (admin/debug
    /// path, not the autosave rotation).
    /// </summary>
    public static bool TryParseReason(string? text, out string reasonName)
    {
        reasonName = "ManualSave";
        if (string.IsNullOrWhiteSpace(text))
            return true;

        switch (text.Trim().ToLowerInvariant())
        {
            case "manual":
            case "manualsave":
            case "1":
                reasonName = "ManualSave";
                return true;
            case "auto":
            case "autosave":
            case "2":
                reasonName = "AutoSave";
                return true;
            case "quick":
            case "quicksave":
            case "0":
                reasonName = "QuickSave";
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Live dedicated root fence. <c>world_save</c> must never overwrite a real world's autosave
    /// on <c>C:\VRisingServer</c>.
    ///
    /// <para><b>The rule is Core's now</b> (job SERVERINST4). Progenitor's graceful
    /// <c>Stop server</c> saves the world from Core, on the same server, and must refuse in exactly
    /// the same places, so the fence moved to <c>Progenitor.Persistence.WorldSaveTrigger</c> and
    /// this method is the delegation. It is a <em>reflective</em> delegation for one reason: that
    /// type is <c>internal</c> to Core and Core grants no <c>InternalsVisibleTo</c> to
    /// <c>Progenitor.Dev</c> (the same reason <see cref="PingIdentity"/> and
    /// <c>ModsUiOps.ReadRoute</c> reflect). Core's assembly is located through a Core type Dev
    /// already binds, not by name, so a renamed or missing method cannot be mistaken for an
    /// allowed path: <b>it fails closed</b> and <c>world_save</c> refuses.</para>
    /// </summary>
    public static bool TryAllowWorldSave(
        string? saveFolder,
        string? processPath,
        string? commandLine,
        out string reason)
    {
        var fence = CoreFence.Method;
        if (fence == null)
        {
            reason = CoreFence.Unavailable;
            return false;
        }

        object?[] args = { saveFolder, processPath, commandLine, null };
        object? allowed;
        try { allowed = fence.Invoke(null, args); }
        catch (Exception e)
        {
            reason = CoreFence.Unavailable + " (" + e.GetType().Name + ")";
            return false;
        }

        reason = args[3] as string ?? "";
        if (allowed is true) return true;
        if (reason.Length == 0) reason = CoreFence.Unavailable;
        return false;
    }

    /// <summary>
    /// The bound <c>WorldSaveTrigger.TryAllowWorldSave</c>, resolved once. Null means the fence
    /// could not be bound, which is a refusal — never a pass.
    /// </summary>
    private static class CoreFence
    {
        internal const string Unavailable =
            "refusing the save: Core's live-dedicated-server fence (Progenitor.Persistence.WorldSaveTrigger.TryAllowWorldSave) could not be bound, and an unbound fence never allows a save";

        internal static readonly System.Reflection.MethodInfo? Method = Resolve();

        private static System.Reflection.MethodInfo? Resolve()
        {
            try
            {
                // HostedPathRules is a Core type Dev already references, so this finds Core's
                // assembly in the shipped two-assembly build and the single test assembly alike.
                var core = typeof(HostedPathRules).Assembly.GetType("Progenitor.Persistence.WorldSaveTrigger", throwOnError: false);
                return core?.GetMethod(
                    "TryAllowWorldSave",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static,
                    binder: null,
                    new[] { typeof(string), typeof(string), typeof(string), typeof(string).MakeByRefType() },
                    modifiers: null);
            }
            catch
            {
                return null;
            }
        }
    }

    /// <summary>
    /// Field-poke overlay of <c>SaveFileName</c> is a 512-byte struct copy. TypeSize 0 has
    /// no memcpy budget and is the Follower-blob class (R12 finding 10); never overlay then.
    /// A positive TypeSize still has to cover <c>CompressSaveFiles</c>. Production never
    /// overlays: typed <c>Write&lt;T&gt;</c> or TriggerSave only.
    /// </summary>
    public static bool MayFieldPokeSaveEvent(int ecsTypeSize)
        => ecsTypeSize >= CompressSaveFilesOffset + 1;

    /// <summary>
    /// Overlay is not a fallback when typed <c>Add</c>/<c>Write</c> of
    /// <c>SavePersistentDataEvent</c> throws. The created entity is destroyed instead.
    /// </summary>
    public static bool UseOverlayWhenTypedWriteFails => false;

    /// <summary>
    /// A 512-byte <c>Buffer.MemoryCopy</c> of <c>SaveFileName</c> is only legal when
    /// <see cref="RawWriteGuard.OverlayMemcpyBudget"/> is at least
    /// <see cref="SaveFileNameBytes"/>. TypeSize 0 has no memcpy budget.
    /// </summary>
    public static bool MayMemcpySaveFileName(int ecsTypeSize)
        => Progenitor.Ecs.RawWriteGuard.OverlayMemcpyBudget(ecsTypeSize) >= SaveFileNameBytes;

    /// <summary>
    /// True when a fallback <c>SavePersistentDataEvent</c> entity was created and the write
    /// did not land: the entity must be destroyed before the op returns, or the next frame
    /// may serialize a garbage name and the one-save-per-frame gate stays closed.
    /// </summary>
    public static bool ShouldDestroyCreatedSaveEvent(bool created, bool writeOk)
        => created && !writeOk;

    /// <summary>
    /// Leaf of <c>GetCombinedFullSavePath</c> (a <c>.save</c> / <c>.save.gz</c> file, not the
    /// folder). Null when the combiner did not return a save file.
    /// </summary>
    public static string? LeafSaveNameFromCombinedPath(string? combined)
    {
        if (string.IsNullOrWhiteSpace(combined)) return null;
        var trimmed = combined.Trim().TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var leaf = Path.GetFileName(trimmed);
        if (string.IsNullOrEmpty(leaf)) return null;
        if (leaf.EndsWith(".save.gz", StringComparison.OrdinalIgnoreCase)
            || leaf.EndsWith(".save", StringComparison.OrdinalIgnoreCase))
            return leaf;
        return null;
    }

    /// <summary>
    /// Highest <c>AutoSave_N.save</c> / <c>.save.gz</c> in a directory listing. Load uses
    /// <c>TryFindMostRecentAutoSave</c> on <c>AutoSave_*</c> only; this is the same name.
    /// </summary>
    public static string? NewestAutoSaveLeaf(System.Collections.Generic.IEnumerable<string>? fileNames)
    {
        if (fileNames == null) return null;
        string? best = null;
        int bestN = int.MinValue;
        foreach (var raw in fileNames)
        {
            if (string.IsNullOrEmpty(raw)) continue;
            var name = Path.GetFileName(raw);
            if (string.IsNullOrEmpty(name)) continue;
            if (!TryParseAutoSaveIndex(name, out var n)) continue;
            if (n < bestN) continue;
            bestN = n;
            best = name;
        }

        return best;
    }

    /// <summary>True when <paramref name="fileName"/> is <c>AutoSave_{n}.save</c> (optional <c>.gz</c>).</summary>
    public static bool TryParseAutoSaveIndex(string? fileName, out int index)
    {
        index = 0;
        if (string.IsNullOrEmpty(fileName)) return false;
        var n = Path.GetFileName(fileName);
        if (n.EndsWith(".gz", StringComparison.OrdinalIgnoreCase))
            n = n.Substring(0, n.Length - 3);
        const string prefix = "AutoSave_";
        const string suffix = ".save";
        if (!n.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            || !n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
            return false;
        var mid = n.Substring(prefix.Length, n.Length - prefix.Length - suffix.Length);
        return int.TryParse(mid, out index);
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Server, main thread. <paramref name="args"/>: optional <c>fileName</c> (leaf ending
    /// in <c>.save</c>; omitted uses the current world save) and optional <c>reason</c>
    /// (<c>ManualSave</c> default, or <c>AutoSave</c> / <c>QuickSave</c>).
    /// Returns <c>{ok, method, fileName, note}</c>. Inner <c>ok: false</c> means the op
    /// ran and the game declined or a gate was closed; the client process throws.
    /// </summary>
    public static object Run(JsonElement args)
    {
        if (!Progenitor.Plugin.IsServerProcess)
            throw new InvalidOperationException("world_save runs on the server side only");

        try
        {
            if (!TryAllowWorldSave(TrySaveFolder(), TryProcessPath(), TryCommandLine(), out var fenceReason))
                return Result(ok: false, "none", "", fenceReason);

            if (!TryParseReason(GetString(args, "reason"), out var reasonName))
                throw new ArgumentException("args.reason must be ManualSave, AutoSave, or QuickSave");
            var reason = ReasonFromName(reasonName);

            var world = Progenitor.Worlds.Server;
            if (world == null || !world.IsCreated)
                return Result(ok: false, "none", "", "no server world yet");

            var em = world.EntityManager;
            int queued = CountQueuedSaves(em);
            var settingsCopied = TryCopySettings(world, out var settings);
            var startup = settingsCopied ? DescribeStartup(settings.StartupState) : "unread";
            bool startupOk = settingsCopied && IsSuccessfulStartup(settings.StartupState);
            bool compress = settingsCopied && settings.CompressSaveFiles;

            string? currentName = null;
            if (settingsCopied)
                currentName = TryCurrentWorldSaveFileName(settings);
            if (string.IsNullOrEmpty(currentName))
                currentName = TryCurrentWorldSaveFileNameFromFolder();
            // A world with no autosave yet (new server, first session after the wipe): the game's first autosave is AutoSave_0.
            if (string.IsNullOrEmpty(currentName))
                currentName = "AutoSave_0.save";

            string fileName;
            try
            {
                fileName = NormalizeSaveFileName(GetString(args, "fileName"), currentName);
            }
            catch (ArgumentException e) when (e.Message.Contains("current world save name is unknown", StringComparison.Ordinal))
            {
                return Result(ok: false, "none", "",
                    "args.fileName omitted and the current world save name could not be read (SaveToLoad / AutoSave_* listing). TerrainManager.SystemData is not read here.");
            }

            var diskName = DiskFileName(fileName, compress);
            var system = TryGetTriggerSystem(world);
            bool? terrainFlag = null;
            try { if (system != null) terrainFlag = system._TerrainManagerInitialized; }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"world_save: reading _TerrainManagerInitialized threw: {e.Message}");
            }

            var gate = $"startup={startup} terrainFlag={(terrainFlag.HasValue ? terrainFlag.Value.ToString() : "unread")} queued={queued} compress={compress}";

            if (queued > 0)
            {
                return Result(ok: false, "none", diskName,
                    $"{gate}; a SavePersistentDataEvent is already queued this frame (one-save-per-frame gate). TerrainManager.SystemData is not read here.");
            }

            if (!settingsCopied)
            {
                return Result(ok: false, "none", diskName,
                    $"{gate}; ServerRuntimeSettings singleton was not readable, so TriggerSave cannot be called. TerrainManager.SystemData is not read here.");
            }

            if (system != null)
            {
                try
                {
                    var fsName = new FixedString128Bytes(fileName);
                    bool accepted = system.TriggerSave(reason, fsName, settings);
                    if (accepted)
                    {
                        FlushModJson();
                        return Result(ok: true, "TriggerSave", diskName,
                            $"{gate}; TriggerSave accepted (SavePersistentDataEvent queued). ModSave.SaveAll chained after the trigger. TerrainManager.SystemData is not read here.");
                    }

                    return Result(ok: false, "TriggerSave", diskName,
                        $"{gate}; TriggerSave returned false. Remaining gate is TerrainManager.SystemData (not read here) or StartupState not SuccessfulStartup. Mod JSON was not flushed.");
                }
                catch (Exception e)
                {
                    Plugin.Logger?.LogWarning(
                        $"world_save: TriggerSave threw ({e.GetType().Name}: {e.Message}); falling back to SavePersistentDataEvent entity.");
                }
            }

            if (!startupOk)
            {
                return Result(ok: false, "none", diskName,
                    $"{gate}; TriggerSave unavailable or threw, and StartupState is not SuccessfulStartup so the entity fallback is skipped. TerrainManager.SystemData is not read here.");
            }

            if (!TryCreateSaveEvent(em, reason, diskName, compress, out var createNote))
            {
                return Result(ok: false, "SavePersistentDataEvent", diskName,
                    $"{gate}; {createNote} TerrainManager.SystemData is not read here.");
            }

            FlushModJson();
            return Result(ok: true, "SavePersistentDataEvent", diskName,
                $"{gate}; {createNote} ModSave.SaveAll chained after the entity create. TerrainManager.SystemData is not read here.");
        }
        catch (Exception e) when (e is not ArgumentException and not InvalidOperationException)
        {
            Plugin.Logger?.LogError($"world_save op threw: {e}");
            throw;
        }
    }

    private static SaveReason ReasonFromName(string name) => name switch
    {
        "AutoSave" => SaveReason.AutoSave,
        "QuickSave" => SaveReason.QuickSave,
        _ => SaveReason.ManualSave,
    };

    private static string? TryCurrentWorldSaveFileName(in ServerRuntimeSettings settings)
    {
        try
        {
            var dir = settings.SaveDirectoryPath.ToString();
            var saveToLoad = "";
            try
            {
                var optional = settings.SaveToLoad;
                if (optional.HasValue)
                    saveToLoad = optional.Value.ToString() ?? "";
            }
            catch (Exception e)
            {
                Plugin.Logger?.LogWarning($"world_save: reading SaveToLoad threw: {e.Message}");
            }

            if (string.IsNullOrWhiteSpace(dir) || string.IsNullOrWhiteSpace(saveToLoad))
                return null;

            var combined = ServerRuntimeSettings.GetCombinedFullSavePath(dir, saveToLoad);
            return LeafSaveNameFromCombinedPath(combined);
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: resolving current save name threw: {e.Message}");
            return null;
        }
    }

    private static string? TryCurrentWorldSaveFileNameFromFolder()
    {
        try
        {
            var folder = ModSave.CurrentSaveDirectory;
            if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
                return null;
            return NewestAutoSaveLeaf(Directory.EnumerateFiles(folder));
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: listing AutoSave_* threw: {e.Message}");
            return null;
        }
    }

    private static string? TrySaveFolder()
    {
        try { return ModSave.CurrentSaveDirectory; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: ModSave.CurrentSaveDirectory threw: {e.Message}");
            return null;
        }
    }

    private static string? TryProcessPath()
    {
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            return proc.MainModule?.FileName;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: process path threw: {e.Message}");
            return null;
        }
    }

    private static string? TryCommandLine()
    {
        try { return Environment.CommandLine; }
        catch { return null; }
    }

    // ---- gates --------------------------------------------------------------------------------

    private static int CountQueuedSaves(EntityManager em)
    {
        EntityQuery query = default;
        bool created = false;
        try
        {
            query = Query.Build(
                em,
                includeDisabled: true,
                includeSystems: false,
                ComponentIndex.Of<SavePersistentDataEvent>(ComponentType.AccessMode.ReadOnly));
            created = true;
            return query.CalculateEntityCount();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: counting SavePersistentDataEvent threw: {e.Message}");
            return 0;
        }
        finally
        {
            if (created)
                query.Dispose();
        }
    }

    private static unsafe bool TryCopySettings(World world, out ServerRuntimeSettings settings)
    {
        settings = default;
        try
        {
            var em = world.EntityManager;
            var componentType = ComponentIndex.Of<ServerRuntimeSettings>(ComponentType.AccessMode.ReadOnly);
            if (!Singletons.TryGetEntity(em, componentType, out var entity))
                return false;
            var index = ComponentIndex.IndexOf<ServerRuntimeSettings>();
            void* raw = em.GetComponentDataRawRO(entity, index);
            if (raw == null)
                return false;
            // Same pointer copy ModSave uses: GetComponentData<ServerRuntimeSettings> is a
            // generic over a game value type, not on the known-good list.
            settings = *(ServerRuntimeSettings*)raw;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"world_save: ServerRuntimeSettings copy threw: {e}");
            settings = default;
            return false;
        }
    }

    private static TriggerPersistenceSaveSystem? TryGetTriggerSystem(World world)
    {
        try
        {
            return world.GetExistingSystemManaged<TriggerPersistenceSaveSystem>();
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"world_save: GetExistingSystemManaged<TriggerPersistenceSaveSystem> threw: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Analyze: CombinedState is LoadPersistentObjects OR InitializeSerializePersistence,
    /// and the ready check is CombinedState == SuccessfulStartup (4). Reading the two fields
    /// avoids the CombinedState getter's il2cpp_runtime_invoke on a struct.
    /// </summary>
    private static bool IsSuccessfulStartup(ServerStartupState state)
    {
        int combined = (int)state.LoadPersistentObjects | (int)state.InitializeSerializePersistence;
        return combined == (int)ServerStartupState.State.SuccessfulStartup;
    }

    private static string DescribeStartup(ServerStartupState state)
    {
        int combined = (int)state.LoadPersistentObjects | (int)state.InitializeSerializePersistence;
        return $"{(ServerStartupState.State)combined} (LoadPersistentObjects={state.LoadPersistentObjects}, InitializeSerializePersistence={state.InitializeSerializePersistence})";
    }

    // ---- entity fallback ----------------------------------------------------------------------

    private static bool TryCreateSaveEvent(
        EntityManager em,
        SaveReason reason,
        string diskName,
        bool compress,
        out string note)
    {
        note = "";
        Entity entity = default;
        bool created = false;
        bool ok = false;
        try
        {
            entity = em.CreateEntity();
            created = true;
            var evt = new SavePersistentDataEvent
            {
                Reason = reason,
                SaveFileName = new FixedString512Bytes(diskName),
                CompressSaveFiles = compress,
            };

            try
            {
                // Add<T>(em, in T) is AddComponent then Write<T> of sizeof(T). RawWriteGuard
                // allows that typed write at TypeSize 0. A 512-byte SaveFileName overlay is
                // never the fallback (R12 finding 10).
                entity.Add(em, in evt);
                note = "created SavePersistentDataEvent via typed Add (TriggerSave threw or the system was missing).";
                ok = true;
                return true;
            }
            catch (Exception typed)
            {
                Plugin.Logger?.LogWarning(
                    $"world_save: typed Add<SavePersistentDataEvent> refused ({typed.Message}); overlay is not used.");
                note =
                    "typed Add/Write of SavePersistentDataEvent refused; overlay is not used " +
                    "(TypeSize 0 would be a 512-byte SaveFileName write). Entity will be destroyed.";
                return false;
            }
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"world_save: creating SavePersistentDataEvent threw: {e}");
            note = $"creating SavePersistentDataEvent threw: {e.GetType().Name}: {e.Message}.";
            return false;
        }
        finally
        {
            if (ShouldDestroyCreatedSaveEvent(created, ok) && em.Exists(entity))
            {
                try { em.DestroyEntity(entity); }
                catch (Exception destroy)
                {
                    Plugin.Logger?.LogWarning($"world_save: DestroyEntity after failed create threw: {destroy.Message}");
                }
            }
        }
    }

    private static void FlushModJson()
    {
        try { ModSave.SaveAll(); }
        catch (Exception e) { Plugin.Logger?.LogError($"world_save: ModSave.SaveAll threw: {e}"); }
    }

    private static object Result(bool ok, string method, string fileName, string note) =>
        new { ok, method, fileName, note };

    private static string? GetString(JsonElement args, string name)
    {
        if (args.ValueKind != JsonValueKind.Object || !args.TryGetProperty(name, out var el))
            return null;
        if (el.ValueKind == JsonValueKind.Null || el.ValueKind == JsonValueKind.Undefined)
            return null;
        try { return el.GetString(); }
        catch { return null; }
    }
#endif
}
