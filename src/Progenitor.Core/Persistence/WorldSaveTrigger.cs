using System;
using System.Collections.Generic;
using System.IO;
using Progenitor.Hosted;
#if !PROGENITOR_TESTS
using ProjectM;
using Progenitor.Ecs;
using Unity.Collections;
using Unity.Entities;
#endif

namespace Progenitor.Persistence;

/// <summary>
/// Job SERVERINST4. The world-save <b>fence</b> and the server-side <b>trigger</b>, in Core,
/// because two callers now need them: the debug bridge's <c>world_save</c> op
/// (<c>Progenitor.Dev/Ops/WorldSaveOps.cs</c>) and Progenitor's own graceful
/// <c>Stop server</c> (<see cref="Progenitor.Install.ServerStopCommand"/>), which must save the
/// world before it quits and must refuse in exactly the same places.
///
/// <para><b>The fence is the reason this file exists.</b> <c>world_save</c> never runs against
/// the live dedicated root, and neither may a stop-triggered save: the refusal is one rule with
/// one sentence, and it now lives on the side that owns every other path rule
/// (<see cref="HostedPathRules"/>). <c>WorldSaveOps.TryAllowWorldSave</c> is a delegation to
/// this method — see the note there for why it is a reflective one — so the Dev tests that
/// pinned the refusal now pin <em>this</em> implementation.</para>
///
/// <para><b>What "saved" means.</b> <c>TriggerSave</c> returning true is an <em>accepted</em>
/// save: it queues a <c>SavePersistentDataEvent</c> that a later frame serializes. Nothing on
/// this process reports the completion of that write, so the observable proof is the save file
/// itself — <see cref="NewestSaveWriteUtc"/> over the current save folder. A stop that quits on
/// acceptance alone would race the serializer and lose the world; so the stop path samples the
/// folder before the trigger, waits for that stamp to advance (<b>off</b> the frame it triggered
/// on, through the frame ticker — a blocking wait here would starve the very system that
/// performs the save), and quits only then. <see cref="SaveConfirmTimeoutSeconds"/> of no
/// advance is a refusal, not a quit.</para>
/// </summary>
internal static class WorldSaveTrigger
{
    /// <summary>How long the stop path waits for the save file's timestamp to advance before refusing.</summary>
    public const int SaveConfirmTimeoutSeconds = 15;

    /// <summary>Seconds between confirmation polls. One per frame batch, not per frame.</summary>
    public const double ConfirmPollSeconds = 0.5;

    /// <summary>
    /// The one refusal sentence for the live dedicated root. Kept verbatim from the Dev op it
    /// moved from: the Dev test asserts the root appears in it, and an operator greps it.
    /// </summary>
    public static string LiveServerRefusal =>
        $"refusing the live dedicated server path ({HostedPathRules.ForbiddenLiveServerRoot}); world_save is never run there";

    /// <summary>
    /// Live dedicated root fence. The save folder, the process image and the command line are all
    /// checked, and the command line is checked as a <em>bounded token</em> so
    /// <c>C:\VRisingServer2</c> is not read as <c>C:\VRisingServer</c>.
    /// </summary>
    public static bool TryAllowWorldSave(
        string? saveFolder,
        string? processPath,
        string? commandLine,
        out string reason)
    {
        if (MentionsLiveRoot(saveFolder)
            || MentionsLiveRoot(processPath)
            || ContainsBoundedToken(commandLine, HostedPathRules.ForbiddenLiveServerRoot))
        {
            reason = LiveServerRefusal;
            return false;
        }

        reason = "";
        return true;
    }

    private static bool MentionsLiveRoot(string? path) =>
        HostedPathRules.IsForbiddenLiveServerPath(path)
        || ContainsBoundedToken(path, HostedPathRules.ForbiddenLiveServerRoot);

    /// <summary>
    /// <paramref name="token"/> appears in <paramref name="text"/> and is not a prefix of a longer
    /// identifier-ish run on either side. A substring match alone would read
    /// <c>C:\VRisingServer-test</c> as the live root; a match that ignores boundaries the other way
    /// would miss <c>-persistentDataPath C:\VRisingServer\save-data</c>, which is the case that
    /// matters.
    /// </summary>
    public static bool ContainsBoundedToken(string? text, string token)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(token)) return false;
        int start = 0;
        while (start < text!.Length)
        {
            int i = text.IndexOf(token, start, StringComparison.OrdinalIgnoreCase);
            if (i < 0) return false;
            bool beforeOk = i == 0 || !IsTokenChar(text[i - 1]);
            int after = i + token.Length;
            bool afterOk = after >= text.Length || !IsTokenChar(text[after]);
            if (beforeOk && afterOk) return true;
            start = i + 1;
        }

        return false;
    }

    private static bool IsTokenChar(char c) => char.IsLetterOrDigit(c) || c == '_';

    /// <summary>Test seam: the folder whose save files prove a save landed. Production is <c>ModSave.CurrentSaveDirectory</c>.</summary>
    internal static Func<string?>? SaveFolderProbe { get; set; }

    /// <summary>Test seam: the newest save-file write stamp in <paramref name="folder"/>. Production lists the folder.</summary>
    internal static Func<string, DateTime>? SaveStampProbe { get; set; }

    internal static void ResetForTests()
    {
        SaveFolderProbe = null;
        SaveStampProbe = null;
    }

    /// <summary>The current world-save folder, or null when this process has none.</summary>
    public static string? SaveFolder()
    {
        if (SaveFolderProbe != null) return SaveFolderProbe();
#if PROGENITOR_TESTS
        return null;
#else
        try { return ModSave.CurrentSaveDirectory; }
        catch (Exception e)
        {
            Plugin.Logger?.LogWarning($"WorldSaveTrigger: ModSave.CurrentSaveDirectory threw: {e.Message}");
            return null;
        }
#endif
    }

    /// <summary>
    /// Newest write stamp among the save files in <paramref name="folder"/>, or
    /// <see cref="DateTime.MinValue"/> when there is nothing to read. Compressed and plain saves
    /// both count (<c>.save</c> / <c>.save.gz</c>); a folder that cannot be listed reads as "no
    /// stamp", which never confirms a save.
    /// </summary>
    public static DateTime NewestSaveWriteUtc(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return DateTime.MinValue;
        if (SaveStampProbe != null)
        {
            try { return SaveStampProbe(folder!); }
            catch { return DateTime.MinValue; }
        }

        try
        {
            if (!Directory.Exists(folder)) return DateTime.MinValue;
            DateTime newest = DateTime.MinValue;
            foreach (string file in Directory.EnumerateFiles(folder!, "*.save*"))
            {
                DateTime stamp;
                try { stamp = File.GetLastWriteTimeUtc(file); }
                catch { continue; }
                if (stamp > newest) newest = stamp;
            }

            return newest;
        }
        catch
        {
            return DateTime.MinValue;
        }
    }

    /// <summary>
    /// True when the save folder's newest write stamp has moved past <paramref name="before"/>,
    /// i.e. the queued save actually serialized. A <see cref="DateTime.MinValue"/> reading — an
    /// unlistable or empty folder — never confirms.
    /// </summary>
    public static bool SaveLanded(string? folder, DateTime before)
    {
        DateTime now = NewestSaveWriteUtc(folder);
        return now != DateTime.MinValue && now > before;
    }

    /// <summary>
    /// The <c>TriggerSave</c> argument for the loaded save <paramref name="saveToLoad"/>: a leaf name ending in <c>.save</c>.
    /// A dedicated server's <c>SaveToLoad</c> is the file it loaded (<c>AutoSave_N.save.gz</c>) and the game appends
    /// <c>.gz</c> itself when it compresses, so the <c>.gz</c> of a trailing <c>.save.gz</c> is stripped first; appending <c>.save</c> to the
    /// whole name wrote <c>AutoSave_N.save.gz.save.gz</c>, a name that grew again at every restart that loaded it.
    /// Same rule as the bridge's <c>world_save</c> (<c>WorldSaveOps.NormalizeSaveFileName</c>). Empty when there is no name.
    /// </summary>
    internal static string TriggerFileName(string? saveToLoad)
    {
        string name = Path.GetFileName((saveToLoad ?? "").Trim());
        if (name.EndsWith(".save.gz", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 3);
        if (string.IsNullOrWhiteSpace(name)) return "";
        if (!name.EndsWith(".save", StringComparison.OrdinalIgnoreCase)) name += ".save";
        return name;
    }

    /// <summary>First autosave name of a world that has none yet: a fresh world's save counter starts at 0.</summary>
    internal const string FreshWorldSaveName = "AutoSave_0.save";

    /// <summary>
    /// The <c>TriggerSave</c> argument for the current world. <paramref name="saveToLoad"/> wins when the world was loaded from a file.
    /// A world that was not (a brand-new server, or the first session after the scheduled wipe archived the old world) has an empty
    /// <c>SaveToLoad</c>; then the newest <c>AutoSave_*</c> by write time in <paramref name="files"/> is used, which is how the game
    /// itself picks the save to load at start, and a folder with none yet gets <see cref="FreshWorldSaveName"/>, the name the
    /// game's own first autosave takes. Other files (<c>SessionId.json</c>, named saves) are ignored.
    /// </summary>
    internal static string ChooseSaveName(string? saveToLoad, IEnumerable<(string Name, DateTime WriteUtc)>? files)
    {
        string fromLoad = TriggerFileName(saveToLoad);
        if (fromLoad.Length > 0) return fromLoad;

        string? best = null;
        DateTime bestStamp = DateTime.MinValue;
        if (files != null)
        {
            foreach (var (raw, stamp) in files)
            {
                string name = Path.GetFileName(raw ?? "");
                if (!name.StartsWith("AutoSave_", StringComparison.OrdinalIgnoreCase)) continue;
                if (!name.EndsWith(".save", StringComparison.OrdinalIgnoreCase)
                    && !name.EndsWith(".save.gz", StringComparison.OrdinalIgnoreCase)) continue;
                if (best != null && stamp <= bestStamp) continue;
                best = name;
                bestStamp = stamp;
            }
        }

        return best == null ? FreshWorldSaveName : TriggerFileName(best);
    }

    /// <summary>The files of <paramref name="folder"/> with their write stamps. An unreadable folder lists as empty.</summary>
    private static List<(string Name, DateTime WriteUtc)> ListSaveFolder(string? folder)
    {
        var list = new List<(string Name, DateTime WriteUtc)>();
        if (string.IsNullOrWhiteSpace(folder)) return list;
        try
        {
            if (!Directory.Exists(folder)) return list;
            foreach (string file in Directory.EnumerateFiles(folder!, "AutoSave_*"))
            {
                try { list.Add((Path.GetFileName(file), File.GetLastWriteTimeUtc(file))); }
                catch { /* one unreadable file is skipped */ }
            }
        }
        catch { /* an unreadable folder behaves like an empty one */ }

        return list;
    }

#if !PROGENITOR_TESTS
    /// <summary>
    /// Ask the game to save the current world, on the server, on the main thread. Returns null
    /// when the save was <b>accepted</b> (a <c>SavePersistentDataEvent</c> is queued and per-mod
    /// JSON has been flushed), or the refusal reason. Acceptance is not completion — see the type
    /// docstring and <see cref="SaveLanded"/>.
    ///
    /// <para>Ordering matches the Dev op it shares the rules with: fence first, then the
    /// <c>ServerRuntimeSettings</c> singleton (no settings, no <c>TriggerSave</c>), then the save name
    /// (<see cref="ChooseSaveName"/>: <c>SaveToLoad</c>, else the newest autosave, else <c>AutoSave_0.save</c>), then
    /// <c>TriggerPersistenceSaveSystem.TriggerSave</c>, then <c>ModSave.SaveAll</c> so mod JSON
    /// lands with the world rather than after the process is gone. There is no entity fallback
    /// here: the bridge op keeps that, because a stop that cannot use the real trigger must refuse
    /// and leave the server running rather than hand-build a save event on the way out.</para>
    /// </summary>
    public static unsafe string? SaveCurrentWorld()
    {
        string? folder = SaveFolder();
        string? processPath = null;
        try
        {
            using var proc = System.Diagnostics.Process.GetCurrentProcess();
            processPath = proc.MainModule?.FileName;
        }
        catch { /* an unreadable image path is not a pass: the other two inputs still fence */ }

        string? commandLine = null;
        try { commandLine = Environment.CommandLine; }
        catch { /* same */ }

        if (!TryAllowWorldSave(folder, processPath, commandLine, out string fenceReason))
            return fenceReason;

        var world = Worlds.Server;
        if (world == null || !world.IsCreated) return "no server world";

        if (!TryCopySettings(world, out var settings))
            return "the ServerRuntimeSettings singleton was not readable, so TriggerSave cannot be called";

        string fileName;
        try
        {
            var optional = settings.SaveToLoad;
            fileName = optional.HasValue ? optional.Value.ToString() ?? "" : "";
        }
        catch (Exception e)
        {
            return "reading SaveToLoad threw: " + e.Message;
        }

        // Empty SaveToLoad (a world not loaded from a save file) falls back to the newest AutoSave_* in the folder, else AutoSave_0.save.
        fileName = ChooseSaveName(fileName, ListSaveFolder(folder));

        TriggerPersistenceSaveSystem? system;
        try { system = world.GetExistingSystemManaged<TriggerPersistenceSaveSystem>(); }
        catch (Exception e) { return "TriggerPersistenceSaveSystem was not reachable: " + e.Message; }
        if (system == null) return "TriggerPersistenceSaveSystem is not present on the server world";

        bool accepted;
        try { accepted = system.TriggerSave(SaveReason.ManualSave, new FixedString128Bytes(fileName), settings); }
        catch (Exception e) { return "TriggerSave threw: " + e.Message; }
        if (!accepted) return "TriggerSave returned false (StartupState or TerrainManager gate)";

        try { ModSave.SaveAll(); }
        catch (Exception e) { Plugin.Logger?.LogError($"WorldSaveTrigger: ModSave.SaveAll threw: {e}"); }
        return null;
    }

    /// <summary>
    /// Pointer copy of the <c>ServerRuntimeSettings</c> singleton — the same one <c>ModSave</c> and
    /// the bridge op use, because <c>GetComponentData&lt;ServerRuntimeSettings&gt;</c> is a generic
    /// over a game value type and is not on the known-good access list.
    /// </summary>
    private static unsafe bool TryCopySettings(World world, out ServerRuntimeSettings settings)
    {
        settings = default;
        try
        {
            var em = world.EntityManager;
            var componentType = ComponentIndex.Of<ServerRuntimeSettings>(ComponentType.AccessMode.ReadOnly);
            if (!Singletons.TryGetEntity(em, componentType, out var entity)) return false;
            void* raw = em.GetComponentDataRawRO(entity, ComponentIndex.IndexOf<ServerRuntimeSettings>());
            if (raw == null) return false;
            settings = *(ServerRuntimeSettings*)raw;
            return true;
        }
        catch (Exception e)
        {
            Plugin.Logger?.LogError($"WorldSaveTrigger: ServerRuntimeSettings copy threw: {e}");
            settings = default;
            return false;
        }
    }
#endif
}
