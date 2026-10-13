// The Edit-and-Continue watcher (roadmap M4 item 5, job FENC2). Host side only: it never
// touches a game process, and the only thing it puts in the reload folder is a .delta file the
// reloader gates exactly like a DLL.
//
//   dotnet run --project tools/enc -- --watch
//   dotnet run --project tools/enc -- --watch --project samples/Progenitor.Sample --once
//
// The loop, which is dotnet watch's HotReload service shape minus the startup hook we cannot
// have (internal research notes):
//
//   1. Keep the last successful Debug compilation of the plugin project, plus an EmitBaseline
//      built from the DEPLOYED image and its portable PDB (the EnC local-slot and lambda maps
//      live in that PDB; without them locals and closures do not survive a delta).
//   2. On a save, re-read the sources and CLASSIFY before compiling anything. EmitDifference
//      does not report rude edits — it throws out of the metadata writer — so the classifier,
//      not the emitter, is what decides. A rude edit is one line, exit code 2, and a "rebuild
//      and redeploy" instruction: the generation swap is still the outer loop.
//   3. A body-only (or body-plus-insert) edit becomes one SemanticEdit per changed method,
//      EmitDifference, and a <plugin>.<n>.delta container in the reload folder.
//   4. The baseline the header names is the sha256 of the deployed image. When that file
//      changes the developer has done a generation swap: the watcher rebaselines and the
//      sequence goes back to 1, because a delta emitted against the old image is meaningless
//      against the new one and the reloader will refuse it.
//   5. Job FENC3: before every emit the watcher reads `<plugin>.delta-state`, the small status
//      file the reloader writes next to the reload folder (baseline sha256 + last applied
//      generation + whether the image was rewritten at load). That is the channel FENC2 did not
//      have. A restart, or a whole session against a server started without the runtime gate,
//      puts the target back at "applied 0" and this side rebaselines to sequence 1 instead of
//      counting up into a permanent out-of-order refusal; a target that applied some deltas and
//      missed others is a generation no file can rebuild, so the watcher says so and waits for
//      the redeploy rather than emitting something the gate would have to refuse.
//   6. Job FENC4 (R39 finding 1): the status file also carries a LOAD NONCE, because `applied=0`
//      is written both by a process that has applied nothing and by one that applied four deltas
//      and then restarted. The watcher keys its accepted snapshot to (baseline sha256, nonce).
//      When the nonce changes the process holds the deployed image and nothing else, so the
//      rebaseline is taken from the DEPLOYED source state and every edit accumulated since the
//      deploy is re-emitted at once as delta 1 — not just the newest one, which would leave the
//      earlier hot edits missing from the process while the log said they were applied.
//
// The Sample must be built Debug for any of this: the runtime refuses to update an optimized
// assembly ("The assembly can not be edited or changed"). `dotnet build` defaults to Debug, so
// the ordinary deploy already produces the right image; -c Release does not.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Progenitor.Enc;

public sealed class EncWatchOptions
{
    /// <summary>Plugin project folder whose *.cs are watched.</summary>
    public string ProjectDir = "";

    /// <summary>The deployed image the target process loaded. Its .pdb must sit next to it.</summary>
    public string BaselineDll = "";

    /// <summary>Where the .delta files are written: the target's BepInEx/progenitor-reload.</summary>
    public string ReloadFolder = "";

    /// <summary>Folders whose *.dll are handed to Roslyn as metadata references.</summary>
    public List<string> ReferenceDirs = new();

    /// <summary>Classify and emit at most one edit, then return. Used by a scripted check.</summary>
    public bool Once;

    public int PollMilliseconds = 500;
}

public static class EncWatch
{
    // Job FENC8: one home for the mapping (EncFailure.cs), which the tests pin.
    public const int ExitOk = EncExitCodes.Ok;
    public const int ExitRude = EncExitCodes.Rude;
    public const int ExitError = EncExitCodes.Error;

    /// <summary>
    /// Run the loop. Returns <see cref="ExitRude"/> when a rude edit ended a <c>--once</c> pass,
    /// so a script can fall back to <c>tools/deploy.ps1</c> without parsing the log.
    /// </summary>
    public static int Run(EncWatchOptions options, Action<string> log)
    {
        if (!Directory.Exists(options.ProjectDir))
        {
            log("enc watch: no such project folder: " + options.ProjectDir);
            return ExitError;
        }
        if (!File.Exists(options.BaselineDll))
        {
            log("enc watch: no baseline image at " + options.BaselineDll
                + " — build the plugin Debug and deploy it first.");
            return ExitError;
        }

        // Job FENC7 (R42 finding 4): `plugin` is the baseline DLL's FILE STEM and it is the ONE
        // identity in this pipeline -- it names the status file this watcher reads, it goes into
        // every delta header, it names every .delta on disk, and it is what the reloader matches
        // against a loaded plugin's stem. The watcher never loads the image, so the simple
        // assembly name is not a name it could use even if it wanted to; the reloader treats that
        // name as informational and publishes it inside the status file for these messages.
        string plugin = System.IO.Path.GetFileNameWithoutExtension(options.BaselineDll);
        // Job FENC8 (evidence 159): the reference folders are swept whole, and BepInEx/core is not
        // all managed. A native image in the set fails the entire compilation with CS0009, so they
        // are probed out here and reported once — the install does not change between polls.
        var referenceSet = EncReferences.Load(options.ReferenceDirs);
        var references = referenceSet.References;
        log("enc watch: " + plugin + " from " + options.ProjectDir
            + " (" + references.Count + " references) -> " + options.ReloadFolder);
        string? skipped = EncReferences.SkippedMessage(referenceSet.Skipped);
        if (skipped != null) log("enc watch: " + skipped);

        Generation? generation;
        try { generation = Rebaseline(options, plugin, references, log); }
        catch (EncCompileException e) { log("enc watch: baseline " + e.Message); return ExitError; }
        catch (Exception e) { log("enc watch: baseline failed: " + e.GetType().Name + ": " + e.Message); return ExitError; }
        if (generation == null) return ExitError;

        IReadOnlyDictionary<string, string> sources = generation!.DeployedSources;
        var lastWrite = Stamp(options.ProjectDir);
        int exit = ExitOk;
        // Job FENC3: the resync and block sentences repeat every poll otherwise. One line per
        // distinct condition, so a developer sees it once and again when it changes.
        string saidOnce = "";
        // Job FENC6 (R41 finding 5): `plugin` here is the baseline DLL's FILE STEM, and that is
        // exactly the key the reloader names this file by -- every writer and the deleter. The
        // watcher was always right to look it up this way; before FENC6 the reloader's success
        // write and unload delete used the simple assembly name instead, so a plugin whose two
        // names differ could leave a loaded=0 refusal here that no later load could clear.
        // Job FENC7 (R42 finding 4) finished the job on the other half of the channel: the
        // deltas themselves are matched and swept by the same stem.
        string statePath = EncDeltaState.PathFor(options.ReloadFolder, plugin);
        // Job FENC4 (R39 finding 1): the load this snapshot is keyed to. The baseline sha alone
        // cannot say which PROCESS is holding that image, and `applied=0` is written by both a
        // fresh load and a restart-after-four; the pair (baseline sha, load nonce) can.
        string syncedNonce = EncDeltaState.Read(statePath)?.LoadNonce ?? "";

        while (true)
        {
            Thread.Sleep(options.PollMilliseconds);

            // A redeploy is a generation swap: the loaded image is not the one the deltas were
            // emitted against any more, so everything restarts from sequence 1.
            if (BaselineChanged(options.BaselineDll, generation!))
            {
                log("enc watch: " + plugin + " was rebuilt; the generation swap is the reload, "
                    + "delta sequence resets to " + 1 + ".");
                try { generation = Rebaseline(options, plugin, references, log); }
                catch (EncCompileException e) { log("enc watch: rebaseline " + e.Message); return ExitError; }
                catch (Exception e) { log("enc watch: rebaseline failed: " + e.Message); return ExitError; }
                if (generation == null) return ExitError;
                sources = generation!.DeployedSources;
                lastWrite = Stamp(options.ProjectDir);
                saidOnce = "";
                // The redeploy carried every accumulated edit into the image, so the snapshot is
                // clean again and the next load's nonce is the one to key it to (job FENC4).
                syncedNonce = EncDeltaState.Read(statePath)?.LoadNonce ?? "";
                continue;
            }

            // Job FENC3 (R38 finding 2): read where the RELOADER actually is before deciding to
            // emit anything. This is the whole answer to "two counters, no channel": the reloader
            // publishes its position per plugin per loaded image and the watcher rebaselines from
            // it on start and after every refusal, because a refusal is exactly the case where
            // the file stops advancing while this side would otherwise keep counting up.
            var state = EncDeltaState.Read(statePath);
            // Job FENC4 (R39 finding 1): a nonce this watcher has not synced to is a DIFFERENT
            // load. Whatever it emitted went to a process that is gone, so the one in front of it
            // holds the deployed image and nothing else.
            bool newLoad = state != null
                && !string.Equals(state.LoadNonce, syncedNonce, StringComparison.Ordinal);
            var resync = EncResyncPolicy.Decide(
                state, plugin, generation!.BaselineSha, generation!.NextSequence, syncedNonce,
                out string resyncMessage);
            if (resync == EncResync.Blocked)
            {
                if (resyncMessage != saidOnce) { log("enc watch: " + resyncMessage); saidOnce = resyncMessage; }
                if (options.Once) return ExitRude;
                continue;
            }
            if (resync == EncResync.RestartFromOne)
            {
                if (resyncMessage != saidOnce) { log("enc watch: " + resyncMessage); saidOnce = resyncMessage; }
                // Rebaseline against the DEPLOYED source state, not the accepted snapshot and not
                // a fresh read (job FENC4, R39 finding 1). The target holds the deployed image, so
                // the deployed sources are what is true of it; the snapshot contains the edits it
                // does NOT have, and rebaselining from it would classify only the newest edit and
                // drop the earlier ones silently while reporting a clean delta 1.
                try { generation = Rebaseline(options, plugin, references, log, generation!.DeployedSources); }
                catch (EncCompileException e) { log("enc watch: rebaseline " + e.Message); return ExitError; }
                catch (Exception e) { log("enc watch: rebaseline failed: " + e.Message); return ExitError; }
                if (generation == null) return ExitError;
                sources = generation!.DeployedSources;
                // A fresh load has none of the accumulated edits, so they are re-emitted NOW as
                // one delta 1 rather than waiting for the next save (which would emit only that
                // save's method). A target that simply refuses everything — the runtime gate off —
                // is the same nonce and waits for a real edit, so this cannot loop.
                if (newLoad) lastWrite = "";
                syncedNonce = state?.LoadNonce ?? "";
            }
            else
            {
                saidOnce = "";
                syncedNonce = state?.LoadNonce ?? syncedNonce;
            }

            string stamp = Stamp(options.ProjectDir);
            if (stamp == lastWrite) continue;
            lastWrite = stamp;

            var updated = ReadSources(options.ProjectDir);
            var edit = EncClassifier.Classify(sources, updated);
            if (edit.Kind == EncEditKind.None) continue;

            // Job FENC3 (R38 finding 5): `sources` is the snapshot the next Classify compares
            // against, so it may advance ONLY when a delta was actually emitted. Folding a rude
            // edit into it made the classifier blind to that edit forever: the next save compared
            // against source the loaded image never had, classified BodyOnly, and compiled the
            // changed signature into the new compilation — either EmitDifference threw on a symbol
            // it could not map, or a body was emitted against a signature the loaded image does
            // not have and the failure surfaced later as a MissingMethodException from a method
            // that "was hot-reloaded successfully". Leaving the snapshot where it is means the
            // same rude edit is reported on every save until the redeploy rebaselines, which is
            // the correct volume for "the running process no longer matches your source".
            if (edit.Kind == EncEditKind.Rude)
            {
                log("enc watch: " + edit.Describe()
                    + " — rebuild and redeploy (tools/deploy.ps1 -Projects " + plugin + ") for a generation swap.");
                exit = ExitRude;
                if (options.Once) return exit;
                continue;
            }

            try
            {
                // Job FENC7 (R42 finding 4): a --once pass prints the identity it emitted under,
                // spelled as what it is. It is the string the reloader matches, so a developer
                // reading "no plugin with that DLL file stem is loaded" has both halves in front
                // of them without reading any source.
                string written = Emit(options, plugin, generation!, updated, edit, log);
                log("enc watch: " + edit.Describe() + " -> " + System.IO.Path.GetFileName(written)
                    + " (plugin stem " + plugin + ")");
                sources = updated;
                exit = ExitOk;
            }
            catch (EncCompileException e)
            {
                // Job FENC8 (evidence 159): the compilation did not BUILD. That is not a statement
                // about the edit — a redeploy would compile the same broken thing — so it exits
                // with the setup code and names the diagnostic that caused it. The snapshot does
                // not advance: nothing was emitted.
                log("enc watch: " + e.Message + " — fix the error (or the reference set); "
                    + "no delta was written.");
                exit = ExitError;
            }
            catch (Exception e)
            {
                // EmitDifference throwing here means the classifier called a rude edit supported.
                // Say so plainly rather than retrying: the developer's fallback is the swap. The
                // snapshot does not advance here either — nothing was emitted, so nothing about
                // the deployed image changed.
                log("enc watch: EmitDifference threw (" + e.GetType().Name + ": " + e.Message
                    + ") — treat as rude: rebuild and redeploy for a generation swap.");
                exit = ExitRude;
            }
            if (options.Once) return exit;
        }
    }

    /// <summary>The last successful compilation, the EnC baseline, and where the sequence is.</summary>
    private sealed class Generation
    {
        public CSharpCompilation Compilation = null!;
        public EmitBaseline Baseline = null!;
        public string BaselineSha = "";
        public long BaselineLength;
        public DateTime BaselineWrite;
        public int NextSequence = 1;
        public MetadataReaderProvider? PdbProvider;
        /// <summary>
        /// The source state the DEPLOYED image was built from — the files as they were when this
        /// baseline was made, which is the last compilation that produced the deployed DLL (job
        /// FENC4, R39 finding 1). It is NOT the accepted snapshot: the snapshot advances on every
        /// emit and describes the running process, this does not move until the image on disk does.
        /// A restart rebaselines from THIS, so the delta that goes to the fresh process carries
        /// every method changed since the deploy and not only the newest edit.
        /// </summary>
        public IReadOnlyDictionary<string, string> DeployedSources =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Rebuild the EnC baseline from the deployed image. <paramref name="withSources"/> is the
    /// source state to compile as the baseline's source: on a resync it is the DEPLOYED source
    /// state (<see cref="Generation.DeployedSources"/>), never the accepted snapshot and never a
    /// fresh read.
    ///
    /// Job FENC4 (R39 finding 1) is the difference between those three. FENC3 passed the accepted
    /// snapshot here and justified it as "the deployed image was built from the last accepted
    /// sources", which is false the moment one delta has been emitted: the snapshot then contains
    /// edits the deployed image does not, so the next Classify sees only the newest edit and the
    /// earlier hot edits are dropped on the floor while the log says delta 1 applied. Re-reading
    /// the files instead would fold the edit that triggered the resync into the baseline it is
    /// about to be classified against, which makes the next diff empty. The deployed source state
    /// is the one that is true of the image the process actually loaded.
    /// </summary>
    private static Generation? Rebaseline(
        EncWatchOptions options, string plugin, IReadOnlyList<MetadataReference> references, Action<string> log,
        IReadOnlyDictionary<string, string>? withSources = null)
    {
        var image = File.ReadAllBytes(options.BaselineDll);
        string pdbPath = System.IO.Path.ChangeExtension(options.BaselineDll, ".pdb");
        if (!File.Exists(pdbPath))
        {
            log("enc watch: " + System.IO.Path.GetFileName(pdbPath) + " is missing. A delta needs the "
                + "baseline's portable PDB (the EnC local-slot and lambda maps live in it); build Debug.");
            return null;
        }
        var pdb = File.ReadAllBytes(pdbPath);

        var sources = withSources ?? ReadSources(options.ProjectDir);
        var compilation = Compile(plugin, sources, references);

        var provider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(pdb, writable: false));
        var reader = provider.GetMetadataReader();
        var baseline = EmitBaseline.CreateInitialBaseline(
            ModuleMetadata.CreateFromImage(image),
            handle => EncProbe.ReadDebugInformation(reader, handle));

        var info = new FileInfo(options.BaselineDll);
        var generation = new Generation
        {
            Compilation = compilation,
            Baseline = baseline,
            BaselineSha = EncDeltaFile.Sha256Hex(image),
            BaselineLength = info.Length,
            BaselineWrite = info.LastWriteTimeUtc,
            NextSequence = 1,
            PdbProvider = provider,
            DeployedSources = sources,
        };
        log("enc watch: baseline " + System.IO.Path.GetFileName(options.BaselineDll)
            + " sha256=" + generation.BaselineSha.Substring(0, 16) + " size=" + info.Length
            + "; deltas start at sequence 1.");
        return generation;
    }

    private static bool BaselineChanged(string path, Generation generation)
    {
        try
        {
            var info = new FileInfo(path);
            return !info.Exists || info.Length != generation.BaselineLength || info.LastWriteTimeUtc != generation.BaselineWrite;
        }
        catch { return false; }
    }

    private static string Emit(
        EncWatchOptions options,
        string plugin,
        Generation generation,
        IReadOnlyDictionary<string, string> sources,
        EncEdit edit,
        Action<string> log)
    {
        var references = generation.Compilation.References.ToList();
        var updated = Compile(plugin, sources, references);

        var edits = new List<SemanticEdit>();
        var names = new List<string>();
        var oldMaps = generation.Compilation.SyntaxTrees.Select(EncClassifier.MethodDeclarations).ToList();
        var newMaps = updated.SyntaxTrees.Select(EncClassifier.MethodDeclarations).ToList();

        foreach (string key in edit.ChangedBodies)
        {
            var oldSymbol = FindSymbol(generation.Compilation, oldMaps, key);
            var newSymbol = FindSymbol(updated, newMaps, key);
            if (oldSymbol == null || newSymbol == null)
                throw new InvalidOperationException("cannot resolve " + key + " in both compilations");
            edits.Add(new SemanticEdit(SemanticEditKind.Update, oldSymbol, newSymbol, preserveLocalVariables: true));
            names.Add(newSymbol.ContainingType?.ToDisplayString() + "." + newSymbol.Name);
        }
        foreach (string key in edit.Inserted)
        {
            var newSymbol = FindSymbol(updated, newMaps, key);
            if (newSymbol == null) continue; // an inserted type or field is carried by the delta anyway
            edits.Add(new SemanticEdit(SemanticEditKind.Insert, null, newSymbol, preserveLocalVariables: false));
            names.Add(newSymbol.ContainingType?.ToDisplayString() + "." + newSymbol.Name);
        }
        if (edits.Count == 0) throw new InvalidOperationException("nothing to emit");

        using var metadata = new MemoryStream();
        using var il = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        var handles = new List<MethodDefinitionHandle>();
#pragma warning disable CS0618 // the non-obsolete overload's UpdatedMethods is not on Roslyn 4.8's public surface
        var difference = updated.EmitDifference(generation.Baseline, edits, metadata, il, pdbDelta, handles);
#pragma warning restore CS0618
        if (!difference.Success)
            throw new InvalidOperationException("EmitDifference failed: "
                + (EncExitCodes.FirstError(difference.Diagnostics) ?? "no diagnostic"));

        var tokens = new List<int>();
        foreach (var handle in handles)
            tokens.Add(MetadataTokens.GetToken(handle));

        var file = EncDeltaFile.Build(
            plugin, generation.BaselineSha, generation.NextSequence, tokens, names,
            metadata.ToArray(), il.ToArray(), pdbDelta.ToArray());
        string written = EncDeltaFile.WriteTo(options.ReloadFolder, plugin, generation.NextSequence, file);

        log("enc watch: delta " + generation.NextSequence + " for " + plugin
            + " (dmeta " + metadata.Length + " B, dil " + il.Length + " B, dpdb " + pdbDelta.Length
            + " B, methods " + handles.Count + ")");

        // The next delta is emitted against THIS one, not against the original image, exactly as
        // dotnet watch chains generations.
        generation.Baseline = difference.Baseline ?? generation.Baseline;
        generation.Compilation = updated;
        generation.NextSequence++;
        return written;
    }

    private static ISymbol? FindSymbol(
        CSharpCompilation compilation, List<Dictionary<string, SyntaxNode>> maps, string key)
    {
        var trees = compilation.SyntaxTrees.ToList();
        for (int i = 0; i < trees.Count && i < maps.Count; i++)
        {
            if (!maps[i].TryGetValue(key, out var node)) continue;
            return compilation.GetSemanticModel(trees[i]).GetDeclaredSymbol(node);
        }
        return null;
    }

    private static CSharpCompilation Compile(
        string assemblyName, IReadOnlyDictionary<string, string> sources, IEnumerable<MetadataReference> references)
    {
        var parse = new CSharpParseOptions(LanguageVersion.Latest);
        var trees = new List<SyntaxTree>();
        foreach (var kv in sources.OrderBy(k => k.Key, StringComparer.Ordinal))
            // Job FENC8: the SourceText must carry an ENCODING. A tree parsed from a bare string
            // has none, and every delta carries debug information, so EmitDifference fails the
            // whole emit with CS8055 "Cannot emit debug information for a source text without
            // encoding" — the next wall behind the reference filter on the live check.
            trees.Add(CSharpSyntaxTree.ParseText(
                Microsoft.CodeAnalysis.Text.SourceText.From(kv.Value, System.Text.Encoding.UTF8),
                parse, kv.Key));
        var compilation = CSharpCompilation.Create(
            assemblyName,
            trees,
            references,
            new CSharpCompilationOptions(
                OutputKind.DynamicallyLinkedLibrary,
                optimizationLevel: OptimizationLevel.Debug,
                allowUnsafe: true));

        // Job FENC8 (evidence 159): ask now, not through EmitDifference. A compilation that does
        // not build is a setup failure with its own exit code, and the diagnostic that says why —
        // a bad reference, a typo in the source — must reach the developer instead of surfacing
        // as "EmitDifference failed", which reads like a rude edit and exits like one.
        string? error = EncExitCodes.FirstError(compilation.GetDiagnostics());
        if (error != null) throw new EncCompileException("compile failed: " + error);
        return compilation;
    }

    private static Dictionary<string, string> ReadSources(string projectDir)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string file in Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories))
        {
            string rel = file.Substring(projectDir.Length).TrimStart('\\', '/');
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            try { map[rel] = File.ReadAllText(file); }
            catch { /* mid-save; the next poll sees it */ }
        }
        return map;
    }

    /// <summary>Cheap change detector: name, length and write time of every watched source.</summary>
    private static string Stamp(string projectDir)
    {
        var sb = new System.Text.StringBuilder();
        foreach (string file in Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories)
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            string rel = file.Substring(projectDir.Length).TrimStart('\\', '/');
            if (rel.StartsWith("bin", StringComparison.OrdinalIgnoreCase)
                || rel.StartsWith("obj", StringComparison.OrdinalIgnoreCase)) continue;
            try
            {
                var info = new FileInfo(file);
                sb.Append(rel).Append(':').Append(info.Length).Append(':')
                  .Append(info.LastWriteTimeUtc.Ticks.ToString(CultureInfo.InvariantCulture)).Append(';');
            }
            catch { sb.Append(rel).Append(":?;"); }
        }
        return sb.ToString();
    }

    // Job FENC8: the reference sweep moved to EncReferences.Load, which probes each candidate for
    // managed metadata first (evidence 159) and is unit-testable without a game install.
}
