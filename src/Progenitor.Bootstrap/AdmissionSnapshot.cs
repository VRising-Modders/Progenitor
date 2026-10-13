using System;
using System.IO;

namespace Progenitor.Bootstrap;

/// <summary>
/// Job HOT2: the narrow Bootstrap admission snapshot the install lane's admission evaluator reads
/// before it will admit a same-session plugin activation.
///
/// <para><b>Why it lives here and why it is this small.</b> The hot-load handoff
/// (2026-09-12, hot-load item 3) asks for "a narrow Bootstrap admission snapshot/API rather than reflecting arbitrary
/// active generations from Core". Bootstrap owns the production reloader
/// (<see cref="ReloaderHost"/>), the Core generation counter (<see cref="CoreLoader"/>), the
/// allowlist (<see cref="ReloadAllowStore"/>) and the process-lifetime UnsafeHarmony table
/// (<see cref="UnsafeHarmonySlot"/>). Core can see none of those directly: it must not
/// compile-reference this assembly, and it is collectible, so it cannot be trusted to describe a
/// generation that is retiring underneath it.</para>
///
/// <para><b>Plain values only.</b> Every member of this type is a <see cref="bool"/>, an
/// <see cref="int"/>, a <see cref="long"/> or a <see cref="string"/>. Nothing here holds an
/// <c>Assembly</c>, a <c>Type</c>, a <c>BasePlugin</c> or a delegate: a snapshot is read by a
/// collectible Core generation and may outlive the generation it describes, and holding any of
/// those would pin exactly the ALC whose retirement this snapshot exists to report.</para>
///
/// <para><b>What it is not.</b> It answers "what is the loader doing right now"; it does not
/// decide anything. The decision is Core's <c>AdmissionEvaluator</c>, over this plus the offline
/// scan and the local reviewed qualification record. It also carries no running-server claim: a
/// configured server folder is not a runtime connection, so the server half of that question is
/// answered by <see cref="ServerProcess"/> alone (this process IS that server, or nothing is
/// known) — the handoff forbids inferring it from a folder.</para>
/// </summary>
/// <remarks>
/// Internal on purpose. Core reads it reflectively by member name (<c>BootstrapLink</c>), which needs
/// no accessibility from the type itself, and keeping it off Bootstrap's public surface keeps it out
/// of the documented API — this is a Core/Bootstrap seam, not something a plugin author calls.
/// <see cref="ProcessHost.CaptureAdmissionSnapshot"/> therefore returns <see cref="object"/>.
/// </remarks>
internal sealed class AdmissionSnapshot
{
    private AdmissionSnapshot(
        bool serverProcess,
        int generation,
        bool loaderBusy,
        bool priorGenerationPinned,
        bool pluginLoaded,
        string pluginName,
        string pluginVersion,
        string pluginImageSha256,
        long pluginImageSize,
        bool unsafeHarmonyMarked,
        bool allowlistAllows,
        string allowlistStatus,
        string reloadFolder,
        bool processLifetimeMarked)
    {
        ProcessLifetimeMarked = processLifetimeMarked;
        ServerProcess = serverProcess;
        Generation = generation;
        LoaderBusy = loaderBusy;
        PriorGenerationPinned = priorGenerationPinned;
        PluginLoaded = pluginLoaded;
        PluginName = pluginName ?? "";
        PluginVersion = pluginVersion ?? "";
        PluginImageSha256 = pluginImageSha256 ?? "";
        PluginImageSize = pluginImageSize;
        UnsafeHarmonyMarked = unsafeHarmonyMarked;
        AllowlistAllows = allowlistAllows;
        AllowlistStatus = allowlistStatus ?? "";
        ReloadFolder = reloadFolder ?? "";
    }

    /// <summary>True in a dedicated or client-hosted server process; false in a game client.</summary>
    public bool ServerProcess { get; }

    /// <summary>
    /// <see cref="CoreLoader.Generation"/>: which Core generation is live. An approval is bound to
    /// this number, and a change between plan and activation invalidates it.
    /// </summary>
    public int Generation { get; }

    /// <summary>
    /// The loader's own busy flag (<see cref="CoreLoader.IsReloading"/>): a Core load, reload or
    /// unload is running right now. Never admit an activation into a swap in flight.
    /// </summary>
    public bool LoaderBusy { get; }

    /// <summary>
    /// A prior generation is still retiring or pinned: either a previous Core ALC is still alive
    /// (<see cref="CoreLoader.PreviousGenerationAlive"/>), a retiring plugin ALC is still latched
    /// (<see cref="ReloaderHost.LatchedPluginAlcsAlive"/>), a same-name plugin ALC already exists
    /// (<see cref="ReloaderHost.ShouldRefuseNewPluginAlc"/>), or a native detour still pins one
    /// (<see cref="ReloaderHost.AnyDetourPinned"/>).
    /// </summary>
    public bool PriorGenerationPinned { get; }

    /// <summary>True when a plugin with the requested GUID is loaded in this process right now.</summary>
    public bool PluginLoaded { get; }

    /// <summary>The loaded plugin's BepInEx name, or "".</summary>
    public string PluginName { get; }

    /// <summary>The loaded plugin's BepInEx version, or "".</summary>
    public string PluginVersion { get; }

    /// <summary>
    /// SHA-256 of the original bytes of the loaded plugin's file in <c>progenitor-reload/</c>, or
    /// "" when it is not loaded from there or the image was compatibility-rewritten (a rewritten
    /// plugin has no on-disk baseline, exactly as <c>ReloaderHost.Loaded.ImageSha</c> records).
    /// </summary>
    public string PluginImageSha256 { get; }

    /// <summary>Byte length of that file, or 0.</summary>
    public long PluginImageSize { get; }

    /// <summary>
    /// <see cref="UnsafeHarmonySlot.IsMarked"/> for the requested GUID: this plugin took
    /// <c>ctx.UnsafeHarmony</c> in this process, so it can never be swapped or removed in place.
    /// The mark is never cleared for the life of the process.
    /// </summary>
    public bool UnsafeHarmonyMarked { get; }

    /// <summary>
    /// Job FINJ1: <see cref="ReloaderHost.IsProcessLifetimePlugin"/> for the requested GUID: the loaded plugin
    /// registered its own IL2CPP types, so like an UnsafeHarmony mark it can never be swapped or removed in place.
    /// </summary>
    public bool ProcessLifetimeMarked { get; }

    /// <summary>
    /// The allowlist verdict for the candidate <c>(fileName, sha256)</c> pair the caller asked
    /// about: true for <see cref="AllowDecision.NoList"/> (no allowlist exists, so the reloader
    /// would load it) and <see cref="AllowDecision.Listed"/>; false for
    /// <see cref="AllowDecision.Unlisted"/> and <see cref="AllowDecision.Malformed"/>. With no
    /// candidate the answer is the same question asked of the loaded image, and "" of both is
    /// <c>Unlisted</c> — fail closed, because admitting bytes the reloader will refuse would
    /// promise an activation that cannot happen.
    /// </summary>
    public bool AllowlistAllows { get; }

    /// <summary><see cref="AllowListStatus"/> as text, for the readback and the refusal detail.</summary>
    public string AllowlistStatus { get; }

    /// <summary><see cref="ReloaderHost.Folder"/>: where an admitted image has to be written.</summary>
    public string ReloadFolder { get; }

    /// <summary>
    /// Capture the loader state for one plugin GUID. See
    /// <see cref="Capture(string,string,string)"/>; this overload asks the allowlist about the
    /// loaded image rather than a candidate, which is the shape a replacement or removal wants.
    /// </summary>
    public static AdmissionSnapshot Capture(string? pluginId) => Capture(pluginId, null, null);

    /// <summary>
    /// Capture the loader state for one plugin GUID, asking the allowlist about the candidate
    /// <paramref name="fileName"/>/<paramref name="sha256"/> the caller intends to write.
    ///
    /// <para>Never throws. Every read is guarded and a read that throws fails closed — busy and
    /// pinned become true, the allowlist becomes false — because a snapshot that cannot see the
    /// loader must not read as a quiet one.</para>
    /// </summary>
    public static AdmissionSnapshot Capture(string? pluginId, string? fileName, string? sha256)
    {
        string id = (pluginId ?? "").Trim();

        bool server;
        try { server = Plugin.IsServerProcess; }
        catch { server = false; }

        int generation;
        try { generation = CoreLoader.Generation; }
        catch { generation = -1; }

        bool busy;
        try { busy = CoreLoader.IsReloading; }
        catch { busy = true; }

        bool loaded = false;
        string name = "", version = "", imageSha = "", loadedFile = "";
        long imageSize = 0;
        try
        {
            loaded = ReloaderHost.TryDescribeLoadedPlugin(id, out name, out version, out imageSha, out loadedFile);
            if (loaded && loadedFile.Length > 0)
            {
                try
                {
                    var info = new FileInfo(loadedFile);
                    if (info.Exists) imageSize = info.Length;
                }
                catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
                {
                    imageSize = 0;
                }
            }
        }
        catch
        {
            // A lookup that throws is not "nothing is loaded": say a plugin is there and let the
            // evaluator refuse with AlreadyLoaded rather than admit an unseen replacement.
            loaded = true;
            name = version = imageSha = "";
        }

        bool pinned;
        try
        {
            pinned = CoreLoader.PreviousGenerationAlive
                || ReloaderHost.LatchedPluginAlcsAlive()
                || ReloaderHost.AnyDetourPinned()
                || ReloaderHost.ShouldRefuseNewPluginAlc(StemOf(fileName) is { Length: > 0 } stem ? stem : StemOf(loadedFile));
        }
        catch { pinned = true; }

        bool marked;
        try { marked = UnsafeHarmonySlot.IsMarked(id); }
        catch { marked = true; }

        string askName = string.IsNullOrWhiteSpace(fileName) ? Path.GetFileName(loadedFile ?? "") : fileName!.Trim();
        string askSha = string.IsNullOrWhiteSpace(sha256) ? imageSha : sha256!.Trim();
        bool allows;
        string status;
        try
        {
            var list = ReloadAllowStore.Current();
            status = list.Status.ToString();
            var decision = list.Decide(askName, askSha);
            allows = decision == AllowDecision.NoList || decision == AllowDecision.Listed;
        }
        catch
        {
            allows = false;
            status = nameof(AllowListStatus.Malformed);
        }

        string folder;
        try { folder = ReloaderHost.Folder; }
        catch { folder = ""; }

        bool lifetime;
        try { lifetime = ReloaderHost.IsProcessLifetimePlugin(id); }
        catch { lifetime = true; }

        return new AdmissionSnapshot(
            server, generation, busy, pinned, loaded, name, version, imageSha, imageSize,
            marked, allows, status, folder, lifetime);
    }

    private static string StemOf(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return "";
        try { return Path.GetFileNameWithoutExtension(path) ?? ""; }
        catch { return ""; }
    }
}
