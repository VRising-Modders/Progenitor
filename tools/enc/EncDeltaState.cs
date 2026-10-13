// The reloader's delta position, read host-side (job FENC3, R38 finding 2).
//
// FENC2 shipped two counters that could not see each other: the reloader's NextDeltaSequence,
// which starts at 1 when a plugin loads and advances only on a successful apply, and the
// watcher's, which starts at 1 when it rebaselines and advances on every EMIT. Three ordinary
// events desynchronized them permanently, and after any of them every save produced
// "sequence 7 is not the next expected 1" forever, with the developer's edit deleted on the way
// past:
//
//   * the server was restarted without a rebuild (the reloader is back at 1, the watcher is at n);
//   * the server was started WITHOUT DOTNET_MODIFIABLE_ASSEMBLIES=debug, so every delta was
//     refused Unsupported and deleted while the watcher counted up;
//   * one ApplyUpdate threw — the classifier's known blind spot — so the reloader did not advance
//     and the watcher did.
//
// The channel is one small status file per plugin, `<stem>.delta-state`, written by the
// reloader NEXT TO the reload folder (never in it, so no scan can pick it up) and only read here.
// `<stem>` is the plugin DLL's FILE STEM, never its simple assembly name (job FENC6, R41
// finding 5): the reloader has to write this file on a load that threw inside LoadFromStream,
// and that is exactly the load with no assembly name to read. The stem is known before anything
// loads, so it is the one key every writer and the deleter can always produce -- which is what
// makes a later successful load of the same DLL overwrite a failed load's refusal instead of
// publishing its health to a second file and leaving the block standing forever. The assembly
// name is recorded INSIDE the file, in `plugin=`, which is where this reader gets it.
// It carries the sha256 of the image that is ACTUALLY LOADED, the LAST APPLIED generation, and
// whether the compatibility rewriter changed the image at load. The writer is
// DeltaApply.FormatState in Core and Bootstrap; this is deliberately not that code, the same way
// the .delta reader is deliberately not EncDeltaFile — and EncHotReloadTests round-trips one
// through the other so the two cannot drift.

using System;
using System.Globalization;
using System.IO;

namespace Progenitor.Enc;

/// <summary>What the reloader last published about one plugin.</summary>
public sealed class EncDeltaState
{
    public const string Magic = "PRGDSTATE1";
    public const string Extension = ".delta-state";

    /// <summary>
    /// Simple assembly name the reloader loaded. This is a FIELD, not the file name: the file is
    /// named by the DLL's stem (job FENC6). On a load that threw before the assembly name could
    /// be read, the reloader records the stem here so the messages below still name something.
    ///
    /// Job FENC7 (R42 finding 4): INFORMATIONAL only. Nothing routes on it — not this file's
    /// name, not a delta's name, not the reloader's match, which all use the stem — so a plugin
    /// whose assembly name differs from its file stem is an ordinary plugin and its deltas apply.
    /// It is here for the sentences below and for a developer reading the file.
    /// </summary>
    public string Plugin = "";

    /// <summary>Lower-hex sha256 of the loaded image, or empty when it was rewritten at load.</summary>
    public string Baseline = "";

    /// <summary>The last generation that actually reached <c>ApplyUpdate</c>. 0 on a fresh load.</summary>
    public int Applied;

    /// <summary>The compatibility rewriter changed the image; this plugin takes no deltas at all.</summary>
    public bool Rewritten;

    /// <summary>
    /// Which load published this (job FENC4, R39 finding 1). Empty when the reloader is older
    /// than the field. The watcher keys its accepted snapshot to
    /// (<see cref="Baseline"/>, this): a nonce it has not synced to means the process was loaded
    /// after the snapshot was taken, so the process holds the deployed image and NOTHING else —
    /// whatever the watcher has already emitted is not in it.
    /// </summary>
    public string LoadNonce = "";

    /// <summary>
    /// Whether the plugin is actually loaded (job FENC5, R40 finding 9). A reloader whose load
    /// THREW writes this file with <c>loaded=0</c> and <see cref="Failure"/> set, instead of
    /// leaving no file at all: the channel stays open so the watcher can print the reason and
    /// block, rather than emitting deltas at a process that holds no such plugin. Defaults to
    /// true, so a reloader older than this field reads exactly as it did before.
    /// </summary>
    public bool Loaded = true;

    /// <summary>Why the load failed, when <see cref="Loaded"/> is false. One line, bounded.</summary>
    public string Failure = "";

    /// <summary>The generation the reloader will accept next.</summary>
    public int Expected => Applied + 1;

    /// <summary><c>Progenitor.Sample.delta-state</c>. Keyed on the DLL's file stem (job FENC6).</summary>
    public static string FileName(string? stem) => (stem ?? "") + Extension;

    /// <summary>
    /// <c>&lt;reload folder's parent&gt;\&lt;stem&gt;.delta-state</c>. <paramref name="stem"/> is the
    /// plugin DLL's file stem -- which is what the watcher has (it derives it from --baseline)
    /// and what every writer in the reloader uses (job FENC6, R41 finding 5).
    /// </summary>
    public static string PathFor(string? reloadFolder, string? stem)
    {
        string folder = reloadFolder ?? "";
        string parent = "";
        try { parent = Path.GetDirectoryName(folder.TrimEnd('\\', '/')) ?? ""; }
        catch { parent = ""; }
        return Path.Combine(parent.Length > 0 ? parent : folder, FileName(stem));
    }

    /// <summary>Read the file, or null when it is missing, unreadable or not a status file.</summary>
    public static EncDeltaState? Read(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            return Parse(File.ReadAllText(path));
        }
        catch { return null; }
    }

    /// <summary>Never throws: a status file half-written by the reloader is simply no answer.</summary>
    public static EncDeltaState? Parse(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var lines = text!.Replace("\r\n", "\n").Split('\n');
        if (lines.Length == 0 || !string.Equals(lines[0].Trim(), Magic, StringComparison.Ordinal)) return null;

        var state = new EncDeltaState();
        bool haveApplied = false;
        for (int i = 1; i < lines.Length; i++)
        {
            string line = lines[i].Trim();
            if (line.Length == 0) continue;
            int eq = line.IndexOf('=');
            if (eq <= 0) continue;
            string key = line.Substring(0, eq);
            string value = line.Substring(eq + 1);
            switch (key)
            {
                case "plugin": state.Plugin = value; break;
                case "baseline": state.Baseline = value.ToLowerInvariant(); break;
                case "applied":
                    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out int applied))
                        return null;
                    state.Applied = applied;
                    haveApplied = true;
                    break;
                case "rewritten": state.Rewritten = value == "1"; break;
                case "nonce": state.LoadNonce = value; break;
                case "loaded": state.Loaded = value != "0"; break;
                case "failure": state.Failure = value; break;
                default: break;
            }
        }
        return haveApplied ? state : null;
    }
}

/// <summary>What the watcher should do with what the reloader published.</summary>
public enum EncResync
{
    /// <summary>The two sides agree; emit as normal. Since job FENC5 this requires a file to
    /// have been read: "nothing to read" is <see cref="Blocked"/>, not agreement.</summary>
    Continue,
    /// <summary>
    /// The target is at generation zero and the watcher is not: it restarted, or it has applied
    /// nothing at all because the runtime gate is off. Rebaseline from the deployed image and go
    /// back to sequence 1 — which is reconstructible precisely because the loaded image IS the
    /// deployed image when nothing has been applied to it.
    ///
    /// Job FENC4 (R39 finding 1): "rebaseline from the deployed image" means from the DEPLOYED
    /// FILE'S SOURCE STATE, not from the watcher's emitted-source snapshot. The snapshot describes
    /// the process the deltas went to, which is the process that just died; compiling the new
    /// baseline from it would classify only the newest edit and silently drop every earlier hot
    /// edit while reporting a clean delta 1.
    /// </summary>
    RestartFromOne,
    /// <summary>
    /// Emitting now would produce a delta the target cannot use, and no rebaseline can fix it:
    /// the loaded image has some of our deltas on it and not others, or is not our image at all.
    /// Say so and wait for the redeploy, which is the outer loop the developer already has.
    /// </summary>
    Blocked,
}

public static class EncResyncPolicy
{
    /// <summary>
    /// The whole resynchronization rule, as a pure function so it can be driven through every
    /// desync R38 finding 2 names without a server.
    ///
    /// The asymmetry between <see cref="EncResync.RestartFromOne"/> and
    /// <see cref="EncResync.Blocked"/> is the point. Roslyn chains generations: to emit N+1 the
    /// watcher needs the baseline that emitting N produced. When the target has applied
    /// <c>0</c> deltas, the image it loaded is the file on disk and the chain can be rebuilt from
    /// scratch — so a restart, or a whole session against a process with the runtime gate off,
    /// costs one rebaseline and nothing else. When the target has applied some and missed others,
    /// the loaded image is a generation the watcher cannot reconstruct from any file, and
    /// pretending otherwise is exactly the corrupt-method-table case the gate exists to prevent.
    /// </summary>
    /// <param name="watcherLoadNonce">
    /// The nonce the watcher's current snapshot is keyed to — the one it read when it last
    /// rebaselined or last agreed with the target (job FENC4, R39 finding 1). A different nonce in
    /// the status file means this is a different load: what the watcher emitted went somewhere
    /// else, and only the deployed image is in front of it now.
    /// </param>
    /// <param name="plugin">
    /// The plugin the watcher is emitting for — the baseline DLL's FILE STEM, which since job
    /// FENC6 (R41 finding 5) is also the name the status file is keyed on. It is named in the
    /// missing-file message (job FENC5, R40 finding 9), it is the only thing there is to say when
    /// there is no file to read, and it is what lets the blocked messages below name the file on
    /// disk rather than leaving an operator to find it.
    /// </param>
    public static EncResync Decide(
        EncDeltaState? state, string? plugin, string watcherBaselineSha, int watcherNextSequence,
        string? watcherLoadNonce, out string message)
    {
        message = "";
        // Job FENC5 (R40 finding 9). No file used to mean Continue, on the reasoning that the
        // target might not be running yet and the watcher should not invent a position. But
        // FENC4's whole mechanism is the nonce, and a missing file carries no nonce: the watcher
        // retained its last one and believed it was in step with a load that was gone. Worse, the
        // failed-load path DELETED this file, so a load that threw silenced the channel forever —
        // every later delta was refused for want of a target, and a refusal with no target writes
        // no file, so it never came back. There is nothing to emit at a plugin that is not there,
        // so say so and stop: this is the developer's outer loop, which is where every
        // unreconstructible case already routes.
        if (state == null)
        {
            message = "no delta-state for " + (string.IsNullOrEmpty(plugin) ? "this plugin" : plugin)
                + " (expected " + EncDeltaState.FileName(plugin) + " next to the reload folder)"
                + ": it is not loaded, the load failed, or EnC is off; fix the load first.";
            return EncResync.Blocked;
        }

        // A load that threw, published as a refusal rather than as silence (job FENC5).
        if (!state.Loaded)
        {
            message = "the target failed to load "
                + (state.Plugin.Length > 0 ? state.Plugin
                   : string.IsNullOrEmpty(plugin) ? "this plugin" : plugin)
                + (state.Failure.Length > 0 ? ": " + state.Failure : "")
                + "; nothing holds this image, so no delta can apply. Fix the load and redeploy "
                // Job FENC6 (R41 finding 5): the redeploy is now enough on its own -- the
                // successful load overwrites this same file, because the reloader keys it on the
                // stem the watcher looks it up by. Naming the file is a belt for the case where
                // the plugin is deliberately retired instead of fixed.
                + "(that load overwrites " + EncDeltaState.FileName(plugin) + "; deleting it by hand clears the "
                + "block too).";
            return EncResync.Blocked;
        }

        if (state.Rewritten)
        {
            message = "the target loaded " + (state.Plugin.Length > 0 ? state.Plugin : "this plugin")
                + " through the compatibility rewriter: image was rewritten at load; deltas need "
                + "the original bytes: set RewriteMode=Off for this loop.";
            return EncResync.Blocked;
        }

        if (!string.Equals(state.Baseline, watcherBaselineSha ?? "", StringComparison.OrdinalIgnoreCase))
        {
            message = "the target has a different image loaded (target " + Short(state.Baseline)
                + ", watcher " + Short(watcherBaselineSha) + "); rebuild and redeploy, or point "
                + "--baseline at the image the target actually loaded.";
            return EncResync.Blocked;
        }

        // Job FENC4 (R39 finding 1). The nonce is what `applied` cannot say: whether the process
        // that is publishing is the process the watcher's snapshot describes.
        bool sameLoad = string.Equals(state.LoadNonce, watcherLoadNonce ?? "", StringComparison.Ordinal);

        // Nothing emitted against this baseline yet, and nothing applied to it: the snapshot IS
        // the deployed source state, so a new load needs no rebaseline — only the nonce, which the
        // caller adopts on Continue. This is the ordinary "watcher started, then the server did".
        if (state.Applied == 0 && watcherNextSequence == 1) return EncResync.Continue;

        if (state.Expected == watcherNextSequence && sameLoad) return EncResync.Continue;

        if (state.Applied == 0)
        {
            message = sameLoad
                ? "the target has applied no deltas to this image (the runtime gate is off, or "
                  + "every delta was refused) and the watcher is at " + watcherNextSequence
                  + "; rebaselining from the deployed sources, delta sequence resets to 1."
                : "the target restarted (new load) and holds only the deployed image, while the "
                  + "watcher is at " + watcherNextSequence + "; rebaselining from the DEPLOYED "
                  + "sources and re-emitting every edit since the deploy, delta sequence resets to 1.";
            return EncResync.RestartFromOne;
        }

        if (!sameLoad)
        {
            message = "the target restarted after applying " + state.Applied
                + " deltas of its own and is now at " + state.Expected + " on a load this watcher "
                + "never emitted against; rebuild and redeploy (the generation swap) to restart the loop.";
            return EncResync.Blocked;
        }

        message = "out of step: the target has applied " + state.Applied
            + " and the watcher has emitted " + (watcherNextSequence - 1)
            + ". The loaded image is a generation no file can rebuild — rebuild and redeploy "
            + "(the generation swap) to restart the loop.";
        return EncResync.Blocked;
    }

    private static string Short(string? sha) =>
        string.IsNullOrEmpty(sha) ? "(none)" : (sha!.Length <= 16 ? sha : sha.Substring(0, 16));
}
