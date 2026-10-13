// Game-free Edit-and-Continue probe (roadmap M4 item 5, job FENC).
//
// Compiles a tiny assembly twice with Roslyn, emits an EnC delta with EmitDifference, loads the
// first image from bytes into a collectible AssemblyLoadContext (the same shape ReloaderHost and
// PluginReloader use), applies the delta with MetadataUpdater.ApplyUpdate and invokes the method
// again to observe the new body. Nothing here touches BepInEx, IL2CPP or a game process.
//
// This file is compiled twice: by tools/enc/Progenitor.EncProbe.csproj (net6.0, the runtime the
// game's CoreCLR is) and, linked, by
// tests/Progenitor.Tests (net8.0). Keep it game-free and dependency-free apart from Roslyn.
//
// ApplyUpdate is gated by the process environment variable DOTNET_MODIFIABLE_ASSEMBLIES=debug,
// read by the native runtime at startup. It cannot be turned on later from managed code and it
// cannot be turned on by a runtimeconfig configProperty; when it is off the probe reports
// Skipped with that sentence, never Failed.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Loader;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace Progenitor.Enc;

public enum EncProbeStatus
{
    /// <summary>The delta was applied and the new method body ran.</summary>
    Applied,
    /// <summary>The runtime does not allow metadata updates in this process. Not a failure.</summary>
    Skipped,
    /// <summary>The runtime allowed the update and it still did not work.</summary>
    Failed,
}

/// <summary>One EnC delta plus the baseline image it was produced against.</summary>
public sealed class EncDelta
{
    public byte[] Image = Array.Empty<byte>();
    public byte[] Pdb = Array.Empty<byte>();
    public byte[] MetadataDelta = Array.Empty<byte>();
    public byte[] IlDelta = Array.Empty<byte>();
    public byte[] PdbDelta = Array.Empty<byte>();
    public int UpdatedMethods;
}

public sealed class EncProbeResult
{
    public EncProbeStatus Status;
    public string Message = "";
    public string? Before;
    public string? After;
    public bool IsSupported;
    public string? EnvironmentValue;
    public bool Collectible;
    public bool? Collected;
    public readonly List<string> Log = new();

    public override string ToString() =>
        Status + ": " + Message + Environment.NewLine + "  " + string.Join(Environment.NewLine + "  ", Log);
}

public static class EncProbe
{
    public const string EnvironmentVariable = "DOTNET_MODIFIABLE_ASSEMBLIES";

    /// <summary>
    /// The sentence a caller prints when the probe or the reloader has to stand down. Nothing a
    /// running process can do enables the feature; only the launcher can.
    /// </summary>
    public const string DisabledMessage =
        "MetadataUpdater.IsSupported is false: this process was not started with " +
        EnvironmentVariable + "=debug, so no method-body delta can be applied. " +
        "Set it in the launcher before the process starts (it cannot be set later, and a " +
        "runtimeconfig configProperty does not enable it).";

    /// <summary>Whether this process may apply metadata updates at all.</summary>
    public static bool IsEnabled => MetadataUpdater.IsSupported;

    public const string BodyV1 = "namespace Probe { public static class Target { public static string Value() { return \"v1\"; } } }";
    public const string BodyV2 = "namespace Probe { public static class Target { public static string Value() { return \"v2\"; } } }";

    private static readonly Guid EncLocalSlotMap = new("6DA9A61E-F8C7-4874-BE62-68BC5630DF71");
    private static readonly Guid EncLambdaAndClosureMap = new("A643004C-0240-496F-A783-30D64F4979DE");

    /// <summary>
    /// The whole experiment: compile, diff, load into a collectible ALC, apply, invoke, unload.
    /// Never throws; an unsupported runtime is <see cref="EncProbeStatus.Skipped"/>.
    /// </summary>
    public static EncProbeResult Run()
    {
        var r = new EncProbeResult();
        r.EnvironmentValue = Environment.GetEnvironmentVariable(EnvironmentVariable);
        r.Log.Add(EnvironmentVariable + "=" + (r.EnvironmentValue ?? "(unset)"));
        r.Log.Add("runtime=" + System.Runtime.InteropServices.RuntimeInformation.FrameworkDescription);
        var present = AppContext.TryGetSwitch("System.Reflection.Metadata.MetadataUpdater.IsSupported", out var sw);
        r.Log.Add($"AppContext switch present={present} value={sw} (a trim substitution, not the gate)");
        r.IsSupported = MetadataUpdater.IsSupported;
        r.Log.Add("MetadataUpdater.IsSupported=" + r.IsSupported);

        EncDelta delta;
        try
        {
            delta = EmitBodyDelta(BodyV1, BodyV2, "Value");
            r.Log.Add($"compiled baseline ({delta.Image.Length} B) and delta " +
                      $"(dmeta {delta.MetadataDelta.Length} B, dil {delta.IlDelta.Length} B, " +
                      $"dpdb {delta.PdbDelta.Length} B, methods {delta.UpdatedMethods})");
        }
        catch (Exception e)
        {
            r.Status = EncProbeStatus.Failed;
            r.Message = "Roslyn EmitDifference failed: " + e.GetType().Name + ": " + e.Message;
            r.Log.Add(r.Message);
            return r;
        }

        if (!r.IsSupported)
        {
            r.Status = EncProbeStatus.Skipped;
            r.Message = DisabledMessage;
            return r;
        }

        var weak = LoadApplyInvoke(delta, r);
        for (var i = 0; i < 10 && weak.IsAlive; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
        r.Collected = !weak.IsAlive;
        r.Log.Add("collectible ALC unloaded=" + r.Collected);
        return r;
    }

    /// <summary>
    /// Load the baseline into a collectible ALC from bytes, apply the delta, invoke again.
    /// NoInlining so no stack slot of this frame keeps the context alive for the collect poll.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference LoadApplyInvoke(EncDelta delta, EncProbeResult r)
    {
        var alc = new AssemblyLoadContext("progenitor-enc-probe", isCollectible: true);
        r.Collectible = alc.IsCollectible;
        var weak = new WeakReference(alc, trackResurrection: true);
        try
        {
            Assembly asm;
            using (var image = new MemoryStream(delta.Image, writable: false))
            using (var pdb = new MemoryStream(delta.Pdb, writable: false))
                asm = alc.LoadFromStream(image, pdb);

            r.Before = (string?)Invoke(asm);
            r.Log.Add("before ApplyUpdate: " + r.Before);
            MetadataUpdater.ApplyUpdate(asm, delta.MetadataDelta, delta.IlDelta, delta.PdbDelta);
            r.After = (string?)Invoke(asm);
            r.Log.Add("after ApplyUpdate: " + r.After);

            r.Status = r.After == "v2" ? EncProbeStatus.Applied : EncProbeStatus.Failed;
            r.Message = r.After == "v2"
                ? "ApplyUpdate replaced the method body of an assembly loaded from bytes into a collectible ALC."
                : "ApplyUpdate returned but the body did not change (still " + r.After + ").";
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            r.Status = EncProbeStatus.Failed;
            r.Message = inner.GetType().Name + ": " + inner.Message;
            r.Log.Add(r.Message);
        }
        finally
        {
            alc.Unload();
        }
        return weak;
    }

    private static object? Invoke(Assembly asm) =>
        asm.GetType("Probe.Target")!.GetMethod("Value")!.Invoke(null, null);

    /// <summary>
    /// Compile <paramref name="v1"/>, compile <paramref name="v2"/> against the same references,
    /// and emit the EnC delta for one <see cref="SemanticEditKind.Update"/> of <paramref name="member"/>.
    /// This half needs no runtime support at all, so a test can always run it.
    /// </summary>
    /// <param name="optimize">
    /// Release baseline. The runtime refuses to update an optimized assembly
    /// ("The assembly can not be edited or changed"), which is why Core and plugins must be
    /// built Debug for this loop.
    /// </param>
    /// <param name="insert">Emit an <see cref="SemanticEditKind.Insert"/> for a new member instead.</param>
    public static EncDelta EmitBodyDelta(string v1, string v2, string member, bool optimize = false, bool insert = false)
    {
        var references = ReferenceSet();
        var options = new CSharpCompilationOptions(
            OutputKind.DynamicallyLinkedLibrary,
            optimizationLevel: optimize ? OptimizationLevel.Release : OptimizationLevel.Debug);
        var parse = new CSharpParseOptions(LanguageVersion.Latest);

        var first = CSharpCompilation.Create("Progenitor.EncProbe.Target", new[] { CSharpSyntaxTree.ParseText(v1, parse) }, references, options);
        using var imageStream = new MemoryStream();
        using var pdbStream = new MemoryStream();
        var emit = first.Emit(imageStream, pdbStream, options: new EmitOptions(debugInformationFormat: DebugInformationFormat.PortablePdb));
        if (!emit.Success) throw new InvalidOperationException("baseline emit failed: " + Join(emit.Diagnostics));

        var result = new EncDelta { Image = imageStream.ToArray(), Pdb = pdbStream.ToArray() };
        var second = first.RemoveAllSyntaxTrees().AddSyntaxTrees(CSharpSyntaxTree.ParseText(v2, parse));

        using var pdbProvider = MetadataReaderProvider.FromPortablePdbStream(new MemoryStream(result.Pdb, writable: false));
        var pdbReader = pdbProvider.GetMetadataReader();
        var baseline = EmitBaseline.CreateInitialBaseline(
            ModuleMetadata.CreateFromImage(result.Image),
            handle => ReadDebugInformation(pdbReader, handle));

        var updated = second.GetTypeByMetadataName("Probe.Target")!.GetMembers(member).First();
        var original = insert ? null : first.GetTypeByMetadataName("Probe.Target")!.GetMembers(member).First();
        var edits = new[]
        {
            new SemanticEdit(insert ? SemanticEditKind.Insert : SemanticEditKind.Update, original, updated, preserveLocalVariables: !insert),
        };

        using var metadata = new MemoryStream();
        using var il = new MemoryStream();
        using var pdbDelta = new MemoryStream();
        var handles = new List<MethodDefinitionHandle>();
        // EmitDifference does NOT validate rude edits: it is the emitter, not the analyzer. Handing
        // it a signature change throws BadImageFormatException out of the metadata writer instead of
        // reporting a diagnostic, so a real watcher must classify the edit before calling this.
#pragma warning disable CS0618 // the non-obsolete overload's UpdatedMethods is not on Roslyn 4.8's public surface
        var difference = second.EmitDifference(baseline, edits, metadata, il, pdbDelta, handles);
#pragma warning restore CS0618
        if (!difference.Success) throw new InvalidOperationException("EmitDifference failed: " + Join(difference.Diagnostics));

        result.MetadataDelta = metadata.ToArray();
        result.IlDelta = il.ToArray();
        result.PdbDelta = pdbDelta.ToArray();
        result.UpdatedMethods = handles.Count;
        return result;
    }

    /// <summary>
    /// Apply an already-emitted delta to a fresh collectible ALC and report what the method
    /// returned, or the exception text. Used by the scenario matrix.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static string ApplyAndInvoke(EncDelta delta)
    {
        if (!MetadataUpdater.IsSupported) return "skipped: " + DisabledMessage;
        var alc = new AssemblyLoadContext("progenitor-enc-scenario", isCollectible: true);
        try
        {
            Assembly asm;
            using (var image = new MemoryStream(delta.Image, writable: false))
            using (var pdb = new MemoryStream(delta.Pdb, writable: false))
                asm = alc.LoadFromStream(image, pdb);
            var before = Invoke(asm);
            MetadataUpdater.ApplyUpdate(asm, delta.MetadataDelta, delta.IlDelta, delta.PdbDelta);
            return before + " -> " + Invoke(asm);
        }
        catch (Exception e)
        {
            var inner = e is TargetInvocationException t && t.InnerException != null ? t.InnerException : e;
            return inner.GetType().Name + ": " + inner.Message;
        }
        finally
        {
            alc.Unload();
        }
    }

    private static string Join(IEnumerable<Diagnostic> diagnostics) =>
        string.Join("; ", diagnostics.Select(d => d.Id + " " + d.GetMessage()));

    /// <summary>
    /// The EnC local-slot and lambda maps Roslyn needs to keep locals and closures aligned across
    /// a delta. They live in the baseline portable PDB as custom debug information.
    /// </summary>
    public static EditAndContinueMethodDebugInformation ReadDebugInformation(MetadataReader pdbReader, MethodDefinitionHandle handle)
    {
        ImmutableArray<byte> slots = default, lambdas = default;
        foreach (var cdiHandle in pdbReader.GetCustomDebugInformation(handle))
        {
            var cdi = pdbReader.GetCustomDebugInformation(cdiHandle);
            var kind = pdbReader.GetGuid(cdi.Kind);
            if (kind == EncLocalSlotMap) slots = pdbReader.GetBlobContent(cdi.Value);
            else if (kind == EncLambdaAndClosureMap) lambdas = pdbReader.GetBlobContent(cdi.Value);
        }
        return EditAndContinueMethodDebugInformation.Create(slots, lambdas);
    }

    /// <summary>
    /// Reference the running runtime's own core assemblies from the TPA list, so the probe needs
    /// no reference-assembly package and works inside a game process as well as a test host.
    /// </summary>
    private static IEnumerable<MetadataReference> ReferenceSet()
    {
        var list = new List<MetadataReference>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (AppContext.GetData("TRUSTED_PLATFORM_ASSEMBLIES") is string tpa && tpa.Length > 0)
        {
            foreach (var path in tpa.Split(Path.PathSeparator))
            {
                var name = Path.GetFileNameWithoutExtension(path);
                if ((name == "System.Private.CoreLib" || name == "System.Runtime" || name == "System.Console")
                    && seen.Add(name) && File.Exists(path))
                    list.Add(MetadataReference.CreateFromFile(path));
            }
        }
        if (list.Count == 0)
            list.Add(MetadataReference.CreateFromFile(typeof(object).Assembly.Location));
        return list;
    }
}
