using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Reflection.Metadata;
using System.Text;
using System.Threading;

namespace Progenitor.Bootstrap;

// MIRROR of src/Progenitor.Core/Reload/DeltaApply.cs, byte-for-byte apart from this header and
// the namespace, under the same rule as ReloadAllowPolicy.cs and PendingInstalls.cs: Bootstrap
// must not compile-reference Core and Core must not compile-reference Bootstrap, so the reloader
// exists twice (ReloaderHost.cs mirrors PluginReloader.cs) and the delta gate it calls has to as
// well. Both copies are compiled by Progenitor.Tests and DeltaApplyTests runs the same table
// through both; ReloadHardeningTests pins them byte-identical. Edit Bootstrap's copy first,
// then re-mirror.
//
// Job FENC2, roadmap M4 item 5. A .delta file in BepInEx/progenitor-reload/ is a method-body
// Edit-and-Continue update for an already-loaded plugin: tools/enc emits it from the same
// sources the loaded image was built from, and the reloader hands the triple inside it to
// MetadataUpdater.ApplyUpdate instead of unloading and reloading the plugin's collectible ALC.
//
// This file is game-free on purpose: Roslyn is not referenced (the delta arrives pre-emitted),
// BepInEx is not referenced, and nothing here touches IL2CPP.

/// <summary>Why a <c>.delta</c> file was applied, or was not (job FENC2).</summary>
public enum DeltaVerdict
{
    /// <summary>Every check passed; the triple may go to <c>MetadataUpdater.ApplyUpdate</c>.</summary>
    Apply,
    /// <summary>The file is not a delta container this build understands.</summary>
    Malformed,
    /// <summary>The process was not started with <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>.</summary>
    Unsupported,
    /// <summary>No plugin with that assembly name is loaded through the reloader.</summary>
    NoPlugin,
    /// <summary>
    /// The compatibility rewriter changed this plugin's image at load, so the bytes in memory are
    /// not the bytes on disk and no delta emitted from source can name the loaded metadata (job
    /// FENC3, R38 finding 1). Nothing about a hash can detect this after the fact — the loader
    /// records it at the moment it happens.
    /// </summary>
    Rewritten,
    /// <summary>The delta was emitted against a different image than the one that is loaded.</summary>
    WrongBaseline,
    /// <summary>Deltas apply in generation order and this is not the next one.</summary>
    OutOfOrder,
    /// <summary>An allowlist exists and this delta is not on it. A delta is arbitrary IL.</summary>
    Unlisted,
}

/// <summary>
/// The parsed head of a <c>.delta</c> container: which plugin, which baseline image, which
/// generation, which methods, and where the three payload spans start.
/// </summary>
public sealed class DeltaHeader
{
    /// <summary>
    /// The plugin DLL's FILE STEM the delta was emitted for — <c>Progenitor.Sample</c> for
    /// <c>Progenitor.Sample.dll</c> — and never its simple assembly name (job FENC7, R42
    /// finding 4).
    ///
    /// One identity, the whole way down this pipeline. The stem is what the watcher derives
    /// (<c>GetFileNameWithoutExtension</c> of the baseline DLL), what names the status file it
    /// reads, what names the <c>.delta</c> on disk, and what
    /// <see cref="DeltaApply.NamesPlugin"/> matches against the loaded plugin's
    /// <c>Stem</c>. FENC6 re-keyed the status file to the stem and left this side matching on
    /// the assembly name, so where the two strings differ the reloader found no target, every
    /// delta was refused for the life of the session, and the status file read perfectly
    /// healthy while it happened. The assembly name is informational everywhere it survives:
    /// the <c>plugin=</c> field inside the status file, and the reloader's load line.
    /// </summary>
    public string Plugin = "";

    /// <summary>Lower-hex SHA-256 of the image the delta was emitted against.</summary>
    public string Baseline = "";

    /// <summary>Generation number, 1 for the first delta after a load.</summary>
    public int Sequence;

    /// <summary>Metadata tokens of the methods whose bodies this delta replaces.</summary>
    public IReadOnlyList<int> Methods = Array.Empty<int>();

    /// <summary>Display names for <see cref="Methods"/>, when the emitter supplied them.</summary>
    public IReadOnlyList<string> Names = Array.Empty<string>();

    /// <summary>Byte offset of the metadata delta inside the container.</summary>
    public int PayloadOffset;

    public int MetadataLength;
    public int IlLength;
    public int PdbLength;

    /// <summary>What the apply log line prints for "methods:" — names if any, else tokens.</summary>
    public string DescribeMethods()
    {
        var parts = new List<string>();
        if (Names.Count > 0)
        {
            for (int i = 0; i < Names.Count; i++) parts.Add(Names[i]);
        }
        else
        {
            for (int i = 0; i < Methods.Count; i++)
                parts.Add("0x" + Methods[i].ToString("x8", CultureInfo.InvariantCulture));
        }
        return parts.Count == 0 ? "(none)" : string.Join(", ", parts);
    }
}

/// <summary>
/// Reads the <c>.delta</c> container tools/enc writes, decides whether it may be applied, and
/// applies it. The decision is separated from the apply so a test can drive the whole table
/// without a runtime that allows metadata updates.
/// </summary>
public static class DeltaApply
{
    /// <summary>First line of every container. Bump with the layout, never in place.</summary>
    public const string Magic = "PRGDELTA1";

    /// <summary>File extension the reload folder scan looks for, next to <c>*.dll</c>.</summary>
    public const string Extension = ".delta";

    /// <summary>The first generation a freshly loaded plugin will accept.</summary>
    public const int FirstSequence = 1;

    /// <summary>
    /// The refusal a plugin whose loaded image is not the file on disk gets, every time, forever
    /// (job FENC3, R38 finding 1). It names the config knob because that is the only thing the
    /// developer can do about it: the rewriter's output cannot be the baseline of a delta emitted
    /// from source, so the choice is the rewrite or the inner loop, never both.
    /// </summary>
    public const string RewrittenMessage =
        "image was rewritten at load; deltas need the original bytes: set RewriteMode=Off for this loop";

    /// <summary>
    /// The status file the reloader writes next to the reload folder, one per plugin, so the
    /// watcher can see where the reloader's sequence actually is (job FENC3, R38 finding 2).
    /// Two counters that cannot see each other desynchronize on the first restart, the first run
    /// without <c>DOTNET_MODIFIABLE_ASSEMBLIES=debug</c>, and the first rude apply; this file is
    /// the channel. It lives NEXT TO the reload folder, never in it, so it is not a file the
    /// reload scan can ever pick up.
    /// </summary>
    public const string StateExtension = ".delta-state";

    /// <summary>First line of the status file. Bump with the layout, never in place.</summary>
    public const string StateMagic = "PRGDSTATE1";

    /// <summary>
    /// The load nonce (job FENC4, R39 finding 1). <c>applied=0</c> is written both when nothing
    /// has ever been applied to this image and when a process that had applied four deltas
    /// restarted, and no counter that resets on load can tell those apart — the restart is
    /// precisely what erased the history the distinction needs. This value changes on EVERY load
    /// of EVERY plugin image and repeats neither within a process nor across a restart, so the
    /// watcher can key its own snapshot to it: a nonce it has not seen means the process in front
    /// of it holds the DEPLOYED image and nothing else, whatever the watcher has emitted.
    ///
    /// The epoch is read once per loaded copy of this type — a Core generation swap loads a new
    /// one, so the per-load counter restarting at 1 cannot collide with a nonce the previous
    /// generation already published.
    /// </summary>
    private static readonly string LoadNonceEpoch =
        DateTime.UtcNow.Ticks.ToString(CultureInfo.InvariantCulture);

    private static int _loadNonceCounter;

    /// <summary>A fresh nonce for one load of one plugin image. See <see cref="LoadNonceEpoch"/>.</summary>
    public static string NextLoadNonce() =>
        LoadNonceEpoch + "-"
        + Interlocked.Increment(ref _loadNonceCounter).ToString(CultureInfo.InvariantCulture);

    /// <summary>
    /// <c>Progenitor.Sample.delta-state</c>. The name is THE FILE STEM of the plugin DLL —
    /// <c>Progenitor.Sample.dll</c> — and never its simple assembly name (job FENC6, R41
    /// finding 5).
    ///
    /// The two are usually the same string and that is exactly why the asymmetry survived
    /// FENC5. They are not the same when they differ, and the one case where the reloader has
    /// to write this file without an assembly name is the case FENC5 exists for: a bad image, a
    /// missing dependency or a rewriter refusal all throw inside <c>LoadFromStream</c>, before
    /// <c>Assembly.GetName()</c> can be read. FENC5 wrote that refusal under the stem and every
    /// path that could clear it — the success write and the unload delete — keyed on the
    /// assembly name, so where the two differed a fixed-and-redeployed plugin published its
    /// healthy state to a different file and the <c>loaded=0</c> refusal survived forever, with
    /// the watcher (which looks the file up by the baseline DLL's stem) blocked on it for good.
    ///
    /// The stem is known before a single byte is loaded, so it is the only key every writer and
    /// every deleter can always produce. The assembly name is not lost: it is recorded INSIDE
    /// the file, in the <c>plugin=</c> field, which is where the watcher reads it for its
    /// messages.
    /// </summary>
    public static string StateFileName(string? stem) =>
        (string.IsNullOrEmpty(stem) ? "<plugin>" : stem) + StateExtension;

    /// <summary>
    /// The status file's full path given the reload folder. The reloader and <c>tools/enc</c> both
    /// derive it this way from the one path they agree on, so neither has to be told where it is.
    /// <paramref name="stem"/> is the plugin DLL's file stem; see <see cref="StateFileName"/>.
    /// </summary>
    public static string StatePathFor(string? reloadFolder, string? stem)
    {
        string folder = reloadFolder ?? "";
        string parent = "";
        try { parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? ""; }
        catch { parent = ""; }
        return Path.Combine(parent.Length > 0 ? parent : folder, StateFileName(stem));
    }

    /// <summary>
    /// Publish one plugin's position (job FENC6, R41 finding 5). This and
    /// <see cref="WriteFailedStateFile"/> and <see cref="DeleteStateFile"/> are the ONLY three
    /// things that touch the status file, they all take the file stem, and they all derive the
    /// path through <see cref="StatePathFor"/> — which is what makes "a later successful load
    /// overwrites the failed state" true by construction rather than by two call sites happening
    /// to pass the same string.
    ///
    /// <paramref name="plugin"/> is the simple assembly name and goes INSIDE the file; it never
    /// names it. Best effort by construction: the folder may be read-only in a locked-down
    /// install, and a missing status file simply leaves the watcher where it was.
    /// </summary>
    public static bool WriteStateFile(
        string? reloadFolder, string? stem, string? plugin, string? baseline, int applied,
        bool rewritten, string? loadNonce)
    {
        if (string.IsNullOrEmpty(stem)) return false;
        try
        {
            File.WriteAllText(
                StatePathFor(reloadFolder, stem),
                FormatState(plugin, baseline, applied, rewritten, loadNonce));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Publish a load that THREW as a refusal rather than as silence (job FENC5), under the file
    /// stem (job FENC6). <paramref name="plugin"/> is the assembly name when the load got far
    /// enough to read one; when it did not — the ordinary case here — the stem stands in for it
    /// in the <c>plugin=</c> field, so the watcher's sentence still names something a developer
    /// recognizes.
    /// </summary>
    public static bool WriteFailedStateFile(
        string? reloadFolder, string? stem, string? plugin, string? reason)
    {
        if (string.IsNullOrEmpty(stem)) return false;
        try
        {
            File.WriteAllText(
                StatePathFor(reloadFolder, stem),
                FormatFailedState(string.IsNullOrEmpty(plugin) ? stem : plugin, reason));
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// The plugin is gone; so is its position. Keyed on the same stem the two writers use, so an
    /// unload clears a <c>loaded=0</c> refusal written by a load whose assembly name was never
    /// readable (job FENC6, R41 finding 5).
    /// </summary>
    public static bool DeleteStateFile(string? reloadFolder, string? stem)
    {
        if (string.IsNullOrEmpty(stem)) return false;
        try { File.Delete(StatePathFor(reloadFolder, stem)); return true; }
        catch { return false; }
    }

    /// <summary>
    /// The status file's body: which plugin, which image is actually loaded, how many deltas have
    /// actually been applied to it, whether the rewriter changed it, and WHICH LOAD this is.
    /// <paramref name="applied"/> is the LAST applied generation, so the next one expected is
    /// <c>applied + 1</c> — a fresh load writes 0 and a restart therefore tells the watcher to go
    /// back to 1.
    ///
    /// <paramref name="loadNonce"/> is the field R39 finding 1 asked for. <c>applied=0</c> on its
    /// own says "this image has nothing on it" without saying whether the watcher's own snapshot
    /// was ever true of it, and a watcher that rebaselines from that snapshot after a restart
    /// emits a delta carrying only the newest edit against a baseline that already contains the
    /// earlier ones: the earlier hot edits are then gone from the process while the log says
    /// applied. The nonce changes on every load, so a watcher that sees a new one knows its
    /// snapshot describes a process that no longer exists.
    /// </summary>
    public static string FormatState(
        string? plugin, string? baseline, int applied, bool rewritten, string? loadNonce = null,
        bool loaded = true, string? failure = null)
    {
        var sb = new StringBuilder();
        sb.Append(StateMagic).Append('\n');
        sb.Append("plugin=").Append(plugin ?? "").Append('\n');
        sb.Append("baseline=").Append((baseline ?? "").ToLowerInvariant()).Append('\n');
        sb.Append("applied=").Append(applied.ToString(CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("rewritten=").Append(rewritten ? "1" : "0").Append('\n');
        sb.Append("nonce=").Append(OneLine(loadNonce)).Append('\n');
        sb.Append("loaded=").Append(loaded ? "1" : "0").Append('\n');
        sb.Append("failure=").Append(Trim(OneLine(failure), 400)).Append('\n');
        return sb.ToString();
    }

    /// <summary>
    /// Job FENC5 (R40 finding 9): the status file a load that THREW leaves behind.
    ///
    /// Before FENC5 the failed-load path routed through <c>Unload(..., Removal)</c>, which
    /// deletes the status file, and a missing file read as <c>Continue</c> — so the watcher went
    /// on emitting deltas against an image no process held, each one refused for want of a
    /// target, and a refusal with no target writes no status file, so the channel never came
    /// back. The desync was permanent and silent.
    ///
    /// The channel now stays open with <c>loaded=0</c> and the reason, which the watcher prints
    /// and blocks on. A missing file is no longer a quiet <c>Continue</c> either: it means the
    /// plugin is not loaded at all (or the reloader is older than this field), and that is the
    /// developer's outer loop, not something the watcher can emit its way out of.
    /// </summary>
    public static string FormatFailedState(string? plugin, string? reason) =>
        FormatState(plugin, baseline: "", applied: 0, rewritten: false, loadNonce: "",
            loaded: false, failure: reason);

    /// <summary>A failure reason is one line and bounded; a whole stack trace is not a channel.</summary>
    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value.Substring(0, max);

    /// <summary>One key, one line: a nonce may not carry a newline into the status file.</summary>
    private static string OneLine(string? value)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(value!.Length);
        foreach (char c in value!)
            if (c != '\n' && c != '\r') sb.Append(c);
        return sb.ToString();
    }

    /// <summary>
    /// The sentence a reloader logs once per process when the runtime gate is off. Nothing a
    /// running process can do turns it on; only the launcher can, before CoreCLR starts.
    /// </summary>
    public const string DisabledMessage =
        "MetadataUpdater.IsSupported is false: this process was not started with " +
        "DOTNET_MODIFIABLE_ASSEMBLIES=debug, so no method-body delta can be applied. " +
        "Set it in the launcher before the process starts (tools/test-server.ps1 -EditAndContinue); " +
        "it cannot be set later, and a runtimeconfig configProperty does not enable it.";

    /// <summary>Whether this process may apply metadata updates at all.</summary>
    public static bool IsSupported => MetadataUpdater.IsSupported;

    /// <summary>
    /// <c>&lt;plugin&gt;.&lt;n&gt;.delta</c> — the name carries the plugin and the generation so a
    /// scan can order the files without opening them. The sequence in the name is advisory; the
    /// header's is the one <see cref="Decide"/> checks.
    /// </summary>
    public static bool TryParseName(string? fileName, out string plugin, out int sequence)
    {
        plugin = "";
        sequence = 0;
        if (string.IsNullOrEmpty(fileName)) return false;
        if (!fileName!.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) return false;
        string stem = fileName.Substring(0, fileName.Length - Extension.Length);
        int dot = stem.LastIndexOf('.');
        if (dot <= 0 || dot == stem.Length - 1) return false;
        if (!int.TryParse(stem.Substring(dot + 1), NumberStyles.None, CultureInfo.InvariantCulture, out sequence))
            return false;
        if (sequence < FirstSequence) return false;
        plugin = stem.Substring(0, dot);
        return plugin.Length > 0;
    }

    /// <summary>
    /// The one delta a scan should take this tick, ordered by <see cref="TryParseName"/>'s parsed
    /// <em>sequence</em> rather than by the file name (job FENC3, R38 finding 3).
    ///
    /// An ordinal sort of the names is generation order only up to nine: <c>X.10.delta</c> sorts
    /// before <c>X.9.delta</c>, so the tenth edit of a session was applied first, refused
    /// out-of-order, and deleted. Two deltas land in one tick whenever two saves do, which a
    /// save-on-focus-loss editor does routinely.
    ///
    /// One per tick, deliberately: applying two in a row means the second's verdict is computed
    /// against a sequence the first just moved, and the scan interval is a second — nothing waits
    /// long. A name that does not parse sorts last and is returned only when nothing else is
    /// there, so it is refused as <see cref="DeltaVerdict.Malformed"/> instead of ordered by
    /// string.
    /// </summary>
    public static string? NextInScanOrder(IReadOnlyList<string>? paths)
    {
        if (paths == null || paths.Count == 0) return null;
        string? best = null;
        bool bestParsed = false;
        string bestPlugin = "";
        int bestSequence = 0;
        for (int i = 0; i < paths.Count; i++)
        {
            string path = paths[i];
            if (string.IsNullOrEmpty(path)) continue;
            string name;
            try { name = Path.GetFileName(path) ?? ""; }
            catch { name = path; }
            bool parsed = TryParseName(name, out string plugin, out int sequence);
            if (best == null) { best = path; bestParsed = parsed; bestPlugin = plugin; bestSequence = sequence; continue; }
            if (parsed != bestParsed)
            {
                if (parsed) { best = path; bestParsed = true; bestPlugin = plugin; bestSequence = sequence; }
                continue;
            }
            if (!parsed)
            {
                if (string.CompareOrdinal(path, best) < 0) best = path;
                continue;
            }
            int byPlugin = string.Compare(plugin, bestPlugin, StringComparison.OrdinalIgnoreCase);
            if (byPlugin < 0 || (byPlugin == 0 && sequence < bestSequence))
            {
                best = path;
                bestPlugin = plugin;
                bestSequence = sequence;
            }
        }
        return best;
    }

    /// <summary>
    /// Parse the container: ASCII header lines, one blank line, then the metadata, IL and PDB
    /// deltas back to back. Never throws; a bad file is <c>false</c> plus a reason.
    /// </summary>
    public static bool TryParse(byte[]? bytes, out DeltaHeader header, out string error)
    {
        header = new DeltaHeader();
        error = "";
        if (bytes == null || bytes.Length == 0) { error = "empty file"; return false; }

        int at = 0;
        var lines = new List<string>();
        while (true)
        {
            if (at >= bytes.Length) { error = "no blank line ends the header"; return false; }
            int end = -1;
            for (int i = at; i < bytes.Length; i++) { if (bytes[i] == (byte)'\n') { end = i; break; } }
            if (end < 0) { error = "no blank line ends the header"; return false; }
            int len = end - at;
            if (len > 0 && bytes[end - 1] == (byte)'\r') len--;
            string line = Encoding.ASCII.GetString(bytes, at, len);
            at = end + 1;
            if (line.Length == 0) break;
            lines.Add(line);
            if (lines.Count > 64) { error = "header is too long"; return false; }
        }

        if (lines.Count == 0 || !string.Equals(lines[0], Magic, StringComparison.Ordinal))
        {
            error = "not a " + Magic + " container";
            return false;
        }

        var methods = new List<int>();
        var names = new List<string>();
        bool haveLengths = false;
        for (int i = 1; i < lines.Count; i++)
        {
            int eq = lines[i].IndexOf('=');
            if (eq <= 0) { error = "header line " + (i + 1) + " is not key=value"; return false; }
            string key = lines[i].Substring(0, eq);
            string value = lines[i].Substring(eq + 1);
            switch (key)
            {
                case "plugin":
                    header.Plugin = value;
                    break;
                case "baseline":
                    header.Baseline = value.ToLowerInvariant();
                    break;
                case "sequence":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int seq))
                    { error = "sequence is not a number"; return false; }
                    header.Sequence = seq;
                    break;
                case "methods":
                    foreach (string token in value.Split(','))
                    {
                        string t = token.Trim();
                        if (t.Length == 0) continue;
                        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t.Substring(2);
                        if (!int.TryParse(t, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int tok))
                        { error = "methods carries a non-hex token"; return false; }
                        methods.Add(tok);
                    }
                    break;
                case "names":
                    foreach (string name in value.Split(','))
                    {
                        string n = name.Trim();
                        if (n.Length > 0) names.Add(n);
                    }
                    break;
                case "lengths":
                    string[] parts = value.Split(',');
                    if (parts.Length != 3
                        || !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out int m)
                        || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out int il)
                        || !int.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out int pdb))
                    { error = "lengths must be three numbers"; return false; }
                    header.MetadataLength = m;
                    header.IlLength = il;
                    header.PdbLength = pdb;
                    haveLengths = true;
                    break;
                default:
                    break;
            }
        }

        if (header.Plugin.Length == 0) { error = "no plugin"; return false; }
        if (header.Baseline.Length != 64 || !IsHex(header.Baseline)) { error = "baseline is not a sha256"; return false; }
        if (header.Sequence < FirstSequence) { error = "sequence must be " + FirstSequence + " or more"; return false; }
        if (!haveLengths) { error = "no lengths"; return false; }
        if (header.MetadataLength <= 0 || header.IlLength <= 0)
        { error = "the metadata and il deltas must both be present"; return false; }

        header.Methods = methods;
        header.Names = names;
        header.PayloadOffset = at;
        long need = (long)at + header.MetadataLength + header.IlLength + header.PdbLength;
        if (need != bytes.Length)
        {
            error = "payload is " + (bytes.Length - at) + " bytes, the header says "
                + (header.MetadataLength + header.IlLength + header.PdbLength);
            return false;
        }
        return true;
    }

    private static bool IsHex(string s)
    {
        for (int i = 0; i < s.Length; i++)
        {
            char c = s[i];
            bool ok = (c >= '0' && c <= '9') || (c >= 'a' && c <= 'f');
            if (!ok) return false;
        }
        return true;
    }

    /// <summary>
    /// Does this delta belong to that loaded plugin? Job FENC7 (R42 finding 4): the ONE identity
    /// test in the delta pipeline, and it compares the header's name — the file stem, see
    /// <see cref="DeltaHeader.Plugin"/> — against the loaded plugin's <c>Stem</c>. Never against
    /// its simple assembly name: the watcher has no way to know that name (it never loads the
    /// image), so a plugin whose two names differ could never be matched, and FENC6's status file
    /// went on reporting a healthy load while every delta was refused.
    ///
    /// Both reloaders call this rather than spelling the comparison out, so the rule cannot drift
    /// between the mirrors again.
    /// </summary>
    public static bool NamesPlugin(string? loadedStem, string? headerPlugin) =>
        !string.IsNullOrEmpty(loadedStem)
        && !string.IsNullOrEmpty(headerPlugin)
        && string.Equals(loadedStem, headerPlugin, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The gate, in the order the refusals should be read: a delta is arbitrary IL, so the
    /// allowlist has the last word, and a delta that is not for this image or not the next
    /// generation is never handed to the runtime (the research note's rule 3 — deltas apply in
    /// generation order and only against the baseline they were emitted from).
    /// </summary>
    /// <param name="loadedBaseline">
    /// SHA-256 of the image the allowlist admitted for this plugin. The reloader clears this to
    /// the empty string when <paramref name="imageWasRewritten"/> is true, so a rewritten plugin
    /// has no baseline any delta can name.
    /// </param>
    /// <param name="imageWasRewritten">
    /// True when the compatibility rewriter returned different bytes than the loader gave it, so
    /// the loaded image is not the file on disk (job FENC3, R38 finding 1). This is <em>not</em>
    /// derivable from a hash: the recorded baseline is the sha256 of the ORIGINAL bytes in both
    /// the rewritten and the unrewritten case, which is exactly the number the watcher computes
    /// from the same file, so the baseline check passed on a rewritten plugin every time. The
    /// loader records the fact at the moment the rewrite happens and it is carried here.
    /// </param>
    public static DeltaVerdict Decide(
        DeltaHeader? header,
        bool isSupported,
        string? loadedBaseline,
        int expectedSequence,
        AllowDecision allow,
        bool imageWasRewritten = false)
    {
        if (header == null) return DeltaVerdict.Malformed;
        if (!isSupported) return DeltaVerdict.Unsupported;
        if (imageWasRewritten) return DeltaVerdict.Rewritten;
        if (string.IsNullOrEmpty(loadedBaseline)) return DeltaVerdict.NoPlugin;
        if (!string.Equals(header.Baseline, loadedBaseline, StringComparison.OrdinalIgnoreCase))
            return DeltaVerdict.WrongBaseline;
        if (header.Sequence != expectedSequence) return DeltaVerdict.OutOfOrder;
        if (!ReloadAllowPolicy.MayLoad(allow)) return DeltaVerdict.Unlisted;
        return DeltaVerdict.Apply;
    }

    /// <summary>The half-sentence a refusal line carries after "refused".</summary>
    public static string RefusalReason(DeltaVerdict verdict, DeltaHeader? header, string? loadedBaseline, int expectedSequence) =>
        verdict switch
        {
            DeltaVerdict.Malformed => "not a readable " + Magic + " container",
            DeltaVerdict.Unsupported => "the runtime does not allow metadata updates",
            // Job FENC7 (R42 finding 4): say WHICH name, because the name is a file stem and the
            // developer's first guess is the assembly name.
            DeltaVerdict.NoPlugin =>
                "no plugin with that DLL file stem is loaded through the reloader",
            DeltaVerdict.Rewritten => RewrittenMessage,
            DeltaVerdict.WrongBaseline =>
                "baseline " + ReloadAllowPolicy.Short(header?.Baseline)
                + " is not the loaded image " + ReloadAllowPolicy.Short(loadedBaseline)
                + "; rebuild and redeploy for a generation swap",
            DeltaVerdict.OutOfOrder =>
                "sequence " + (header?.Sequence ?? 0) + " is not the next expected " + expectedSequence
                // Job FENC6 (R41 finding 5): the status file is named for the plugin DLL's FILE
                // STEM, and a delta header carries the assembly name, so this cannot spell the
                // file out. It names the extension and where to look instead, which is true of
                // every plugin.
                + "; the watcher resyncs from the " + StateExtension
                + " file next to the reload folder (last applied " + (expectedSequence - 1)
                + "), or rebuild and redeploy to reset the sequence",
            DeltaVerdict.Unlisted =>
                "allowlist=" + ReloadAllowPolicy.Token(AllowDecision.Unlisted)
                + "; a delta is code and is gated like one",
            _ => "applied",
        };

    /// <summary>
    /// <c>Reload: applied delta 3 to Progenitor.Sample (methods: Probe.Target.Value); baseline=ba7816bf8f01cfea</c>
    /// </summary>
    public static string FormatApplied(DeltaHeader header) =>
        "Reload: applied delta " + header.Sequence + " to " + header.Plugin
        + " (methods: " + header.DescribeMethods() + "); baseline=" + ReloadAllowPolicy.Short(header.Baseline);

    /// <summary>
    /// <c>Reload: delta Progenitor.Sample.3.delta refused (sequence 3 is not the next expected 2); deleted, nothing applied.</c>
    /// </summary>
    public static string FormatRefused(string? fileName, string reason) =>
        "Reload: delta " + (string.IsNullOrEmpty(fileName) ? "(unnamed)" : fileName)
        + " refused (" + reason + "); deleted, nothing applied.";

    /// <summary>
    /// <c>Reload: delta Progenitor.Sample.3.delta was rude (…); nothing applied — rebuild and
    /// redeploy the plugin for a generation swap.</c> The watcher classifies before it emits, so
    /// this is the case where its classification and the runtime's disagreed.
    /// </summary>
    public static string FormatRude(string? fileName, string message) =>
        "Reload: delta " + (string.IsNullOrEmpty(fileName) ? "(unnamed)" : fileName)
        + " was rude (" + message + "); nothing applied — rebuild and redeploy the plugin "
        + "for a generation swap.";

    /// <summary>
    /// Hand the three spans to the runtime. Returns false with the exception text when the
    /// runtime refuses the edit (a rude delta the watcher's classifier let through, an optimized
    /// baseline, a missed generation): the caller logs and changes nothing, so the developer's
    /// normal deploy still produces a generation swap.
    /// </summary>
    public static bool TryApply(Assembly? assembly, DeltaHeader header, byte[] file, out string error)
    {
        error = "";
        if (assembly == null) { error = "no assembly"; return false; }
        try
        {
            var metadata = new ReadOnlySpan<byte>(file, header.PayloadOffset, header.MetadataLength);
            var il = new ReadOnlySpan<byte>(file, header.PayloadOffset + header.MetadataLength, header.IlLength);
            var pdb = new ReadOnlySpan<byte>(
                file, header.PayloadOffset + header.MetadataLength + header.IlLength, header.PdbLength);
            MetadataUpdater.ApplyUpdate(assembly, metadata, il, pdb);
            return true;
        }
        catch (Exception e)
        {
            error = e.GetType().Name + ": " + e.Message;
            return false;
        }
    }
}
